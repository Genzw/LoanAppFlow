using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LoanApp.IntegrationTests;

public sealed class PolicyConcurrencyTests : IsolatedPolicyDatabase
{
    private static PolicyService Service(AppDbContext db) => new(new EfPolicyStore(db), new RuleEngine(), TimeProvider.System);
    private static PolicyDocument Empty => new(1, [], []);

    [Fact]
    public async Task Simultaneous_initializers_commit_exactly_one_baseline_and_audit()
    {
        var gate = new CommitGate();
        await using var first = Open(gate); await using var second = Open(gate);
        var results = await Task.WhenAll(Service(first).Seed(Empty, Guid.NewGuid(), default), Service(second).Seed(Empty, Guid.NewGuid(), default));
        Assert.Single(results, value => value);
        await using var verify = Open(); var head = await verify.PolicyHeads.SingleAsync();
        Assert.Equal(head.ActiveRevisionId, head.BaselineRevisionId); Assert.NotNull(head.ActiveRevisionId); Assert.Null(head.DraftRevisionId);
        Assert.Equal(2, head.Version); Assert.Single(await verify.PolicyRevisions.ToListAsync()); Assert.Single(await verify.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Initializer_and_operator_race_preserves_the_winner_without_orphans()
    {
        await using var read = Open(); var head = await new EfPolicyStore(read).Head(default);
        var gate = new CommitGate(); await using var seedDb = Open(gate); await using var editorDb = Open(gate);
        var seed = Service(seedDb).Seed(Empty, Guid.NewGuid(), default);
        var editor = Capture(async () => { await Service(editorDb).CreateDraft(null, PolicyTags.Head(head), Guid.NewGuid(), default); });
        await Task.WhenAll(seed, editor);
        await using var verify = Open(); var actual = await new EfPolicyStore(verify).Head(default);
        Assert.Equal(2, actual.Version); Assert.Single(await verify.PolicyRevisions.ToListAsync()); Assert.Single(await verify.AuditEvents.ToListAsync());
        if (await seed) { Assert.Equal(409, (await editor)!.Status); Assert.Null(actual.DraftRevisionId); Assert.NotNull(actual.BaselineRevisionId); }
        else { Assert.Null(await editor); Assert.NotNull(actual.DraftRevisionId); Assert.Null(actual.ActiveRevisionId); Assert.Null(actual.BaselineRevisionId); }
    }

    [Fact]
    public async Task Publishing_and_editing_the_same_draft_cannot_both_commit()
    {
        await using var setup = Open(); var service = Service(setup); await service.Seed(Empty, Guid.NewGuid(), default);
        var original = await new EfPolicyStore(setup).Head(default);
        var draft = await service.CreateDraft(original.ActiveRevisionId, PolicyTags.Head(original), Guid.NewGuid(), default);
        var gate = new CommitGate(); await using var publisher = Open(gate); await using var editor = Open(gate);
        var results = await Task.WhenAll(
            Capture(async () => { await Service(publisher).Publish(PolicyTags.Draft(draft.Revision), draft.Head.Version, Guid.NewGuid(), default); }),
            Capture(async () => { await Service(editor).AddBlacklist("000000004", PolicyTags.Draft(draft.Revision), Guid.NewGuid(), default); }));
        Assert.Single(results, e => e is null); Assert.Equal(412, Assert.Single(results, e => e is not null)!.Status);
        await using var verify = Open(); var head = await new EfPolicyStore(verify).Head(default);
        var revision = (await new EfPolicyStore(verify).Revision(draft.Revision.Id, default))!;
        Assert.Equal(2, revision.DraftVersion); Assert.Equal(3, await verify.AuditEvents.CountAsync());
        if (results[0] is null) { Assert.Null(head.DraftRevisionId); Assert.Equal(draft.Revision.Id, head.ActiveRevisionId); Assert.Empty(revision.Document.Blacklist); }
        else { Assert.Equal(original.ActiveRevisionId, head.ActiveRevisionId); Assert.Equal(draft.Revision.Id, head.DraftRevisionId); Assert.Single(revision.Document.Blacklist); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_save_acknowledgement_is_recovered_only_if_atomic_audit_is_visible(bool committed)
    {
        await using var db = Open(new LostAcknowledgement(committed)); var service = Service(db);
        if (committed) Assert.True(await service.Seed(Empty, Guid.NewGuid(), default));
        else
        {
            var error = await Assert.ThrowsAsync<PolicyError>(() => service.Seed(Empty, Guid.NewGuid(), default));
            Assert.Equal(503, error.Status); Assert.Equal("OUTCOME_UNKNOWN", error.Code);
        }
        await using var verify = Open();
        Assert.Equal(committed ? 1 : 0, await verify.PolicyRevisions.CountAsync());
        Assert.Equal(committed ? 1 : 0, await verify.AuditEvents.CountAsync());
        Assert.Equal(committed ? 2 : 1, (await verify.PolicyHeads.SingleAsync()).Version);
    }

    private static async Task<PolicyError?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (PolicyError error) { return error; } }

    [Fact]
    public async Task Sql_failure_during_publication_preserves_active_and_draft_on_another_connection()
    {
        await using var setup = Open(); var service = Service(setup); await service.Seed(Empty, Guid.NewGuid(), default);
        var original = await new EfPolicyStore(setup).Head(default);
        var draft = await service.CreateDraft(original.ActiveRevisionId, PolicyTags.Head(original), Guid.NewGuid(), default);
        await using var failing = Open(new RejectAudit());
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(failing).Publish(PolicyTags.Draft(draft.Revision), draft.Head.Version, Guid.NewGuid(), default));
        await using var verify = Open();
        Assert.Equal(draft.Head, await new EfPolicyStore(verify).Head(default));
        var unchanged = (await new EfPolicyStore(verify).Revision(draft.Revision.Id, default))!;
        Assert.Equal("Draft", unchanged.Kind); Assert.Null(unchanged.PublishedAtUtc); Assert.Equal(1, unchanged.DraftVersion);
        Assert.Equal(2, await verify.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Publish_and_discard_compete_without_partial_state()
    {
        await using var setup = Open(); var service = Service(setup); await service.Seed(Empty, Guid.NewGuid(), default);
        var original = await new EfPolicyStore(setup).Head(default);
        var draft = await service.CreateDraft(original.ActiveRevisionId, PolicyTags.Head(original), Guid.NewGuid(), default);
        var gate = new CommitGate(); await using var publisher = Open(gate); await using var discarder = Open(gate);
        var results = await Task.WhenAll(
            Capture(async () => { await Service(publisher).Publish(PolicyTags.Draft(draft.Revision), draft.Head.Version, Guid.NewGuid(), default); }),
            Capture(() => Service(discarder).Discard(PolicyTags.Draft(draft.Revision), Guid.NewGuid(), default)));
        Assert.Single(results, e => e is null); Assert.Contains(Assert.Single(results, e => e is not null)!.Status, new[] { 409, 412 });
        await using var verify = Open(); var store = new EfPolicyStore(verify); var head = await store.Head(default);
        Assert.Null(head.DraftRevisionId); Assert.Equal(draft.Head.Version + 1, head.Version);
        Assert.Equal(3, await verify.AuditEvents.CountAsync());
        if (results[0] is null) { Assert.Equal(draft.Revision.Id, head.ActiveRevisionId); Assert.Equal("Published", (await store.Revision(draft.Revision.Id, default))!.Kind); }
        else { Assert.Equal(original.ActiveRevisionId, head.ActiveRevisionId); Assert.Null(await store.Revision(draft.Revision.Id, default)); }
    }

    private sealed class RejectAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<AuditEvent>()) entry.Entity.Action = new string('x', 81);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
    private sealed class LostAcknowledgement(bool afterSave) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            afterSave ? ValueTask.FromResult(result) : throw new TimeoutException("Injected before database save");
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new TimeoutException("Injected after committed database save");
    }
}
