using System.Text.Json;
using System.Text.RegularExpressions;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;

namespace LoanApp.Core.Application;

public sealed record PolicyPointers(long Version, Guid? ActiveRevisionId, Guid? DraftRevisionId, Guid? BaselineRevisionId);
public sealed record PolicySnapshot(Guid Id, string Kind, Guid? BaseRevisionId, long DraftVersion,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc, PolicyDocument Document);
public sealed record PolicyAudit(string Action, Guid EntityId, Guid CorrelationId, string ActorType, int RulesCount, int BlacklistCount,
    long? PreviousDraftVersion = null, long? DraftVersion = null, Guid[]? AffectedRuleIds = null);
public sealed record PolicyMutation(PolicyPointers HeadBefore, PolicyPointers? HeadAfter,
    PolicySnapshot? RevisionBefore, PolicySnapshot? RevisionAfter, PolicyAudit Audit);
public sealed class PolicyConflict(bool headChanged) : Exception("Concurrent policy change")
{
    public bool HeadChanged { get; } = headChanged;
}
public sealed class PolicyError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public interface IPolicyStore
{
    Task<PolicyPointers> Head(CancellationToken ct);
    Task<PolicySnapshot?> Revision(Guid id, CancellationToken ct);
    Task<IReadOnlyList<PolicySnapshot>> History(int take, DateTimeOffset? before, Guid? beforeId, CancellationToken ct);
    Task Commit(PolicyMutation mutation, CancellationToken ct);
    Task AuditSimulation(PolicyAudit audit, CancellationToken ct);
}
public sealed record PolicySummary(int RulesAdded, int RulesChanged, int RulesRemoved, int BlacklistAdded,
    int BlacklistRemoved, Guid[] AffectedRuleIds, bool? HasBaselineDifferences);
public sealed record PolicyValidation(bool Valid, object[] Errors, string[] Warnings, long DraftVersion,
    long HeadVersion, Guid? ActiveRevisionId, PolicySummary Summary);

public static partial class PolicyTags
{
    public static string Head(PolicyPointers head) => $"\"head-{head.Version}\"";
    public static string Draft(PolicySnapshot revision) => $"\"draft-{revision.Id:D}-{revision.DraftVersion}\"";
    public static void Require(string? supplied, string expected, bool draft)
    {
        if (string.IsNullOrWhiteSpace(supplied)) throw new PolicyError(428, "IF_MATCH_REQUIRED");
        if (!(draft ? DraftPattern() : HeadPattern()).IsMatch(supplied)) throw new PolicyError(400, "INVALID_ETAG");
        if (!string.Equals(supplied, expected, StringComparison.Ordinal)) throw new PolicyError(412, "STALE_VERSION");
    }
    [GeneratedRegex("^\"head-[1-9][0-9]*\"$")] private static partial Regex HeadPattern();
    [GeneratedRegex("^\"draft-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-[1-9][0-9]*\"$")] private static partial Regex DraftPattern();
}

public sealed class PolicyService(IPolicyStore store, RuleEngine engine, TimeProvider clock)
{
    public async Task<PolicySnapshot> Get(Guid id, CancellationToken ct) =>
        await store.Revision(id, ct) ?? throw new PolicyError(404, "REVISION_NOT_FOUND");

    public async Task<(PolicySnapshot Revision, PolicyPointers Head)> CreateDraft(Guid? sourceId, string? etag, Guid correlationId, CancellationToken ct)
    {
        var head = await store.Head(ct); PolicyTags.Require(etag, PolicyTags.Head(head), false);
        if (head.DraftRevisionId is not null) throw new PolicyError(409, "DRAFT_ALREADY_EXISTS");
        var document = new PolicyDocument(1, [], []);
        if (sourceId is { } id)
        {
            var source = await Get(id, ct);
            if (source.Kind != "Published") throw new PolicyError(409, "SOURCE_NOT_PUBLISHED");
            document = source.Document;
        }
        var draft = new PolicySnapshot(Guid.NewGuid(), "Draft", sourceId, 1, clock.GetUtcNow(), null, document);
        var after = head with { Version = head.Version + 1, DraftRevisionId = draft.Id };
        await Save(new(head, after, null, draft, Audit(sourceId is null ? "Policy.DraftCreated" : "Policy.DraftCopied", draft, correlationId)), ct);
        return (draft, after);
    }

    public async Task<PolicySnapshot> ReplaceRules(RuleDefinition[] rules, string? tag, Guid correlationId, CancellationToken ct)
    {
        var (head, before) = await Draft(tag, ct);
        var after = before with { DraftVersion = before.DraftVersion + 1,
            Document = PolicyValidator.Normalize(before.Document with { Rules = rules }) };
        await Save(new(head, null, before, after, Audit("Policy.RulesChanged", after, correlationId)), ct);
        return after;
    }

    public async Task<(PolicySnapshot Revision, BlacklistEntry Entry)> AddBlacklist(string ssn, string? tag, Guid correlationId, CancellationToken ct)
    {
        var (head, before) = await Draft(tag, ct); var normalized = SubmissionValidator.NormalizeSsn(ssn);
        if (before.Document.Blacklist.Any(e => e.Ssn == normalized)) throw new PolicyError(409, "BLACKLIST_DUPLICATE");
        var entry = new BlacklistEntry(Guid.NewGuid(), normalized);
        var after = before with { DraftVersion = before.DraftVersion + 1,
            Document = PolicyValidator.Normalize(before.Document with { Blacklist = [.. before.Document.Blacklist, entry] }) };
        await Save(new(head, null, before, after, Audit("Policy.BlacklistAdded", after, correlationId)), ct);
        return (after, entry);
    }
    public async Task<PolicySnapshot> RemoveBlacklist(Guid id, string? tag, Guid correlationId, CancellationToken ct)
    {
        var (head, before) = await Draft(tag, ct);
        if (!before.Document.Blacklist.Any(e => e.Id == id)) throw new PolicyError(404, "BLACKLIST_ENTRY_NOT_FOUND");
        var after = before with { DraftVersion = before.DraftVersion + 1,
            Document = PolicyValidator.Normalize(before.Document with { Blacklist = before.Document.Blacklist.Where(e => e.Id != id).ToArray() }) };
        await Save(new(head, null, before, after, Audit("Policy.BlacklistRemoved", after, correlationId)), ct);
        return after;
    }
    public async Task Discard(string? tag, Guid correlationId, CancellationToken ct)
    {
        var (head, draft) = await Draft(tag, ct);
        await Save(new(head, head with { Version = head.Version + 1, DraftRevisionId = null }, draft, null,
            Audit("Policy.DraftDiscarded", draft, correlationId)), ct);
    }

    public async Task<PolicyValidation> Validate(string? tag, CancellationToken ct)
    {
        var (head, draft) = await Draft(tag, ct);
        PolicyValidator.Normalize(draft.Document);
        var active = head.ActiveRevisionId is { } activeId ? (await Get(activeId, ct)).Document : new(1, [], []);
        var baseline = head.BaselineRevisionId is { } baselineId ? (await Get(baselineId, ct)).Document : null;
        var oldRules = active.Rules.ToDictionary(r => r.Id); var newRules = draft.Document.Rules.ToDictionary(r => r.Id);
        var added = newRules.Keys.Except(oldRules.Keys).ToArray(); var removed = oldRules.Keys.Except(newRules.Keys).ToArray();
        var changed = newRules.Keys.Intersect(oldRules.Keys).Where(id => JsonSerializer.Serialize(newRules[id], PolicyJson.Options) != JsonSerializer.Serialize(oldRules[id], PolicyJson.Options)).ToArray();
        var oldList = active.Blacklist.Select(e => e.Ssn).ToHashSet(StringComparer.Ordinal);
        var newList = draft.Document.Blacklist.Select(e => e.Ssn).ToHashSet(StringComparer.Ordinal);
        bool? baselineDifferent = baseline is null ? null : Canonical(baseline) != Canonical(draft.Document);
        List<string> warnings = [];
        if (!draft.Document.Rules.Any(r => r.Enabled)) warnings.Add("APPROVES_ALL_VALID_SUBMISSIONS");
        if (baselineDifferent is true) warnings.Add("BASELINE_DIFFERS");
        return new(true, [], warnings.ToArray(), draft.DraftVersion, head.Version, head.ActiveRevisionId,
            new(added.Length, changed.Length, removed.Length, newList.Except(oldList).Count(), oldList.Except(newList).Count(),
                [.. added, .. changed, .. removed], baselineDifferent));
    }

    public async Task<PolicyPointers> Publish(string? tag, long expectedHeadVersion, Guid correlationId, CancellationToken ct)
    {
        var (head, draft) = await Draft(tag, ct);
        if (head.Version != expectedHeadVersion) throw new PolicyError(409, "HEAD_CHANGED");
        var published = draft with { Kind = "Published", DraftVersion = draft.DraftVersion + 1,
            PublishedAtUtc = clock.GetUtcNow(), Document = PolicyValidator.Normalize(draft.Document) };
        var after = head with { Version = head.Version + 1, ActiveRevisionId = draft.Id, DraftRevisionId = null };
        await Save(new(head, after, draft, published, Audit("Policy.Published", published, correlationId)), ct);
        return after;
    }

    public async Task<(Evaluation Evaluation, long DraftVersion)> Simulate(Guid id, Submission input, string? tag, Guid correlationId, CancellationToken ct)
    {
        var revision = await Get(id, ct);
        if (revision.Kind == "Draft") PolicyTags.Require(tag, PolicyTags.Draft(revision), true);
        var evaluation = engine.Evaluate(revision.Id, revision.Document, input);
        try { await store.AuditSimulation(Audit("Policy.Simulated", revision, correlationId), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new PolicyError(503, "AUDIT_UNAVAILABLE"); }
        return (evaluation, revision.DraftVersion);
    }

    public async Task<bool> Seed(PolicyDocument document, Guid correlationId, CancellationToken ct)
    {
        var head = await store.Head(ct);
        if (head.ActiveRevisionId is not null || head.DraftRevisionId is not null || head.BaselineRevisionId is not null || (await store.History(1, null, null, ct)).Count > 0) return false;
        var revision = new PolicySnapshot(Guid.NewGuid(), "Published", null, 1, clock.GetUtcNow(), clock.GetUtcNow(), PolicyValidator.Normalize(document));
        try
        {
            await store.Commit(new(head, head with { Version = head.Version + 1, ActiveRevisionId = revision.Id, BaselineRevisionId = revision.Id }, null, revision,
                Audit("Policy.Initialized", revision, correlationId) with { ActorType = "Initializer" }), ct);
            return true;
        }
        catch (PolicyConflict) { return false; }
    }
    private async Task<(PolicyPointers Head, PolicySnapshot Revision)> Draft(string? tag, CancellationToken ct)
    {
        var head = await store.Head(ct);
        if (head.DraftRevisionId is not { } id) throw new PolicyError(409, "DRAFT_NOT_AVAILABLE");
        var revision = await store.Revision(id, ct);
        if (revision?.Kind != "Draft") throw new PolicyError(409, "DRAFT_NOT_AVAILABLE");
        PolicyTags.Require(tag, PolicyTags.Draft(revision), true);
        return (head, revision);
    }
    private async Task Save(PolicyMutation mutation, CancellationToken ct)
    {
        var oldRules = mutation.RevisionBefore?.Document.Rules.ToDictionary(r => r.Id) ?? [];
        var newRules = mutation.RevisionAfter?.Document.Rules.ToDictionary(r => r.Id) ?? [];
        var changed = oldRules.Keys.Union(newRules.Keys).Where(id => !oldRules.ContainsKey(id) || !newRules.ContainsKey(id) ||
            JsonSerializer.Serialize(oldRules[id], PolicyJson.Options) != JsonSerializer.Serialize(newRules[id], PolicyJson.Options)).ToArray();
        mutation = mutation with { Audit = mutation.Audit with { PreviousDraftVersion = mutation.RevisionBefore?.DraftVersion, AffectedRuleIds = changed } };
        try { await store.Commit(mutation, ct); }
        catch (PolicyConflict conflict) { throw new PolicyError(conflict.HeadChanged ? 409 : 412, conflict.HeadChanged ? "HEAD_CHANGED" : "STALE_VERSION"); }
    }
    private static PolicyAudit Audit(string action, PolicySnapshot revision, Guid correlationId) =>
        new(action, revision.Id, correlationId, "Local", revision.Document.Rules.Length, revision.Document.Blacklist.Length, DraftVersion: revision.DraftVersion);
    private static string Canonical(PolicyDocument document) => PolicyJson.Write(document with
    {
        Rules = document.Rules.OrderBy(r => r.Id).ToArray(), Blacklist = document.Blacklist.OrderBy(e => e.Id).ToArray()
    });
}
