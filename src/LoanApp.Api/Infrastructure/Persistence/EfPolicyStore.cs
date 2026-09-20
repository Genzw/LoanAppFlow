using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Core.Rules;
using LoanApp.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LoanApp.Api.Infrastructure.Persistence;

public sealed class EfPolicyStore(AppDbContext db) : IPolicyStore
{
    public async Task<PolicyPointers> Head(CancellationToken ct)
    {
        var head = await db.PolicyHeads.AsNoTracking().SingleAsync(h => h.Id == 1, ct);
        return new(head.Version, head.ActiveRevisionId, head.DraftRevisionId, head.BaselineRevisionId);
    }
    public async Task<PolicySnapshot?> Revision(Guid id, CancellationToken ct)
    {
        var revision = await db.PolicyRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return revision is null ? null : Snapshot(revision);
    }
    public async Task<IReadOnlyList<PolicySnapshot>> History(int take, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var query = db.PolicyRevisions.AsNoTracking().Where(r => r.Kind == "Published");
        if (before is { } date && beforeId is { } id)
            query = query.Where(r => r.PublishedAtUtc < date || (r.PublishedAtUtc == date && r.Id.CompareTo(id) < 0));
        var rows = await query.OrderByDescending(r => r.PublishedAtUtc).ThenByDescending(r => r.Id).Take(take).ToArrayAsync(ct);
        return rows.Select(Snapshot).ToArray();
    }
    public async Task Commit(PolicyMutation mutation, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (mutation.HeadAfter is { } headAfter)
        {
            var head = new PolicyHead { Id = 1, Version = mutation.HeadBefore.Version,
                ActiveRevisionId = mutation.HeadBefore.ActiveRevisionId, DraftRevisionId = mutation.HeadBefore.DraftRevisionId, BaselineRevisionId = mutation.HeadBefore.BaselineRevisionId };
            db.Attach(head); head.Version = headAfter.Version; head.ActiveRevisionId = headAfter.ActiveRevisionId;
            head.DraftRevisionId = headAfter.DraftRevisionId; head.BaselineRevisionId = headAfter.BaselineRevisionId;
        }
        if (mutation.RevisionBefore is { } before)
        {
            if (before.Kind != "Draft") throw new InvalidOperationException("Published revision is immutable");
            var entity = Entity(before); db.Attach(entity);
            if (mutation.RevisionAfter is { } after)
            {
                entity.Kind = after.Kind; entity.DocumentJson = PolicyJson.Write(after.Document);
                entity.DraftVersion = after.DraftVersion; entity.PublishedAtUtc = after.PublishedAtUtc;
            }
            else db.Remove(entity);
        }
        else if (mutation.RevisionAfter is { } created) db.PolicyRevisions.Add(Entity(created));
        var audit = Audit(mutation.Audit);
        db.AuditEvents.Add(audit);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException conflict)
        { throw new PolicyConflict(conflict.Entries.Any(e => e.Entity is PolicyHead)); }
        catch (Exception error) when (error is NpgsqlException and not PostgresException ||
            error is TimeoutException || error is DbUpdateException { InnerException: NpgsqlException and not PostgresException })
        {
            // The audit ID belongs to this attempt and commits atomically with every mutation.
            // Recover on a fresh connection; never assume a lost commit acknowledgement means rollback.
            try
            {
                await using var recovery = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(db.Database.GetConnectionString()).Options);
                if (await recovery.AuditEvents.AnyAsync(e => e.Id == audit.Id, ct)) return;
            }
            catch (Exception recoveryError) when (recoveryError is not OperationCanceledException) { }
            throw new PolicyError(503, "OUTCOME_UNKNOWN");
        }
    }
    public async Task AuditSimulation(PolicyAudit audit, CancellationToken ct)
    { db.AuditEvents.Add(Audit(audit)); await db.SaveChangesAsync(ct); }
    private static PolicySnapshot Snapshot(PolicyRevision r)
    {
        try { return new(r.Id, r.Kind, r.BaseRevisionId, r.DraftVersion, r.CreatedAtUtc, r.PublishedAtUtc, PolicyJson.Read(r.DocumentJson)); }
        catch (Exception e) when (e is JsonException or ValidationFailure) { throw new PolicyError(503, "POLICY_UNAVAILABLE"); }
    }
    private static PolicyRevision Entity(PolicySnapshot r) => new() { Id = r.Id, Kind = r.Kind, BaseRevisionId = r.BaseRevisionId,
        DraftVersion = r.DraftVersion, CreatedAtUtc = r.CreatedAtUtc, PublishedAtUtc = r.PublishedAtUtc,
        SchemaVersion = r.Document.SchemaVersion, DocumentJson = PolicyJson.Write(r.Document) };
    private static AuditEvent Audit(PolicyAudit a) => new() { Id = Guid.NewGuid(), Component = "Api", Action = a.Action,
        CorrelationId = a.CorrelationId, ActorType = a.ActorType, EntityType = "PolicyRevision", EntityId = a.EntityId,
        Outcome = "Succeeded", PolicyRevisionId = a.EntityId,
        MetadataJson = JsonSerializer.Serialize(new { rulesCount = a.RulesCount, blacklistCount = a.BlacklistCount,
            previousDraftVersion = a.PreviousDraftVersion, draftVersion = a.DraftVersion, affectedRuleIds = a.AffectedRuleIds ?? [] }) };
}
