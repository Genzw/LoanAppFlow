using System.Text.Json;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace LoanApp.IntegrationTests;

public sealed class ApplicationTests : IsolatedPolicyDatabase
{
    private static Submission Form => new("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Empresa A", 100, "000000003");
    private static PolicyDocument Policy => new(1, [new(Guid.NewGuid(), "STATE_NY", "State", "Estado no admitido", true, 10, "Deny", "ALL",
        [new("address.state", "equals", JsonSerializer.SerializeToElement("NY"))])], []);
    private async Task Seed()
    { await using var db = Open(); await new PolicyService(new EfPolicyStore(db), new RuleEngine(), TimeProvider.System).Seed(Policy, Guid.NewGuid(), default); }
    private async Task<ApplicationResult> Submit(Submission input, params IInterceptor[] interceptors)
    {
        await using var db = Open();
        return await new ApplicationService(new EfPolicyStore(db), new EfApplicationStore(Options(interceptors), TimeProvider.System), new RuleEngine())
            .Submit(input, Guid.NewGuid(), default);
    }
    [Fact]
    public async Task Recurrence_preserves_ids_increments_versions_and_captures_immutable_events()
    {
        await Seed(); var first = (await Submit(Form)).Approved!;
        var second = (await Submit(Form with { Ssn = "000-00-0003", CompanyName = "Empresa B", RequestedAmount = 200 })).Approved!;
        Assert.Equal(first.CustomerId, second.CustomerId); Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.Equal(2, second.ApplicationVersion); Assert.Equal("Updated", second.Operation); Assert.NotEqual(first.EventId, second.EventId);
        await using var db = Open(); Assert.Single(await db.Customers.ToListAsync()); Assert.Single(await db.Applications.ToListAsync());
        var events = await db.OutboxMessages.OrderBy(e => e.ApplicationVersion).ToArrayAsync(); Assert.Equal(2, events.Length);
        Assert.All(events, e => Assert.Equal("Pending", e.Status));
        var snapshots = events.Select(e => JsonSerializer.Deserialize<ApplicationEvent>(e.PayloadJson, PolicyJson.Options)!).ToArray();
        Assert.Equal("Empresa A", snapshots[0].Customer.CompanyName); Assert.Equal(100, snapshots[0].Application.RequestedAmount);
        Assert.Equal("Empresa B", snapshots[1].Customer.CompanyName); Assert.Equal(200, snapshots[1].Application.RequestedAmount);
        var denial = (await Submit(Form with { Address = Form.Address with { State = "NY" } })).Denied!;
        Assert.Single(denial.Reasons); Assert.Equal(2, await db.OutboxMessages.CountAsync());
        Assert.Equal("Empresa B", (await db.Customers.SingleAsync()).CompanyName);
        var metadata = string.Join("", await db.AuditEvents.Select(a => a.MetadataJson).ToArrayAsync());
        Assert.DoesNotContain(Form.Ssn, metadata); Assert.DoesNotContain("Empresa", metadata);
        Assert.Equal(10, await db.AuditEvents.CountAsync()); // seed, 4+4 approvals, denial
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_presentations_preserve_complete_pairs_and_consecutive_events(bool existing)
    {
        await Seed(); if (existing) await Submit(Form);
        var gate = new Gate();
        var results = await Task.WhenAll(Submit(Form with { CompanyName = "One", RequestedAmount = 111 }, gate),
            Submit(Form with { CompanyName = "Two", RequestedAmount = 222 }, gate));
        Assert.All(results, r => Assert.NotNull(r.Approved));
        Assert.Equal(results[0].Approved!.CustomerId, results[1].Approved!.CustomerId);
        await using var db = Open(); var customer = await db.Customers.SingleAsync(); var application = await db.Applications.SingleAsync();
        Assert.Equal(customer.CompanyName == "One" ? 111 : 222, application.RequestedAmount);
        var events = await db.OutboxMessages.OrderBy(e => e.ApplicationVersion).ToArrayAsync();
        Assert.Equal(existing ? 3 : 2, events.Length);
        Assert.Equal(Enumerable.Range(1, events.Length).Select(i => (long)i), events.Select(e => e.ApplicationVersion));
        foreach (var e in events.Skip(existing ? 1 : 0))
        { var p = JsonSerializer.Deserialize<ApplicationEvent>(e.PayloadJson, PolicyJson.Options)!; Assert.Equal(p.Customer.CompanyName == "One" ? 111 : 222, p.Application.RequestedAmount); }
    }
    [Theory]
    [InlineData("customer", false)] [InlineData("application", false)] [InlineData("outbox", false)] [InlineData("audit", false)]
    [InlineData("customer", true)] [InlineData("application", true)] [InlineData("outbox", true)] [InlineData("audit", true)]
    public async Task Sql_failure_rolls_back_all_rows_on_create_and_update(string target, bool existing)
    {
        await Seed(); if (existing) await Submit(Form);
        await Assert.ThrowsAsync<DbUpdateException>(() => Submit(Form with { CompanyName = "Changed", RequestedAmount = 222 }, new InvalidRow(target)));
        await using var verify = Open();
        Assert.Equal(existing ? 1 : 0, await verify.Customers.CountAsync()); Assert.Equal(existing ? 1 : 0, await verify.Applications.CountAsync());
        Assert.Equal(existing ? 1 : 0, await verify.OutboxMessages.CountAsync()); Assert.Equal(existing ? 5 : 1, await verify.AuditEvents.CountAsync());
        if (existing) { Assert.Equal("Empresa A", (await verify.Customers.SingleAsync()).CompanyName); Assert.Equal(100, (await verify.Applications.SingleAsync()).RequestedAmount); }
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Unknown_commit_is_recovered_by_event_id_without_replay(bool afterSave)
    {
        await Seed();
        if (afterSave) Assert.NotNull((await Submit(Form, new LostAck(afterSave))).Approved);
        else Assert.Equal("OUTCOME_UNKNOWN", (await Assert.ThrowsAsync<PolicyError>(() => Submit(Form, new LostAck(afterSave)))).Code);
        await using var verify = Open(); Assert.Equal(afterSave ? 1 : 0, await verify.OutboxMessages.CountAsync());
        Assert.Equal(afterSave ? 5 : 1, await verify.AuditEvents.CountAsync());
    }
    [Fact]
    public async Task Invalid_form_missing_policy_and_failed_denial_audit_never_create_business_rows()
    {
        Assert.Equal(503, (await Assert.ThrowsAsync<PolicyError>(() => Submit(Form))).Status);
        await Assert.ThrowsAsync<ValidationFailure>(() => Submit(Form with { RequestedAmount = 1.001m }));
        await Seed();
        Assert.Equal("AUDIT_UNAVAILABLE", (await Assert.ThrowsAsync<PolicyError>(() => Submit(Form with { Address = Form.Address with { State = "NY" } }, new InvalidRow("audit")))).Code);
        await using var verify = Open(); Assert.Empty(await verify.Customers.ToListAsync()); Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Single(await verify.AuditEvents.ToListAsync());
    }
    [Fact]
    public async Task Publication_during_inflight_submission_does_not_change_its_captured_policy()
    {
        await Seed(); var pause = new Pause(); var pending = Submit(Form, pause);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var db = Open(); var policies = new PolicyService(new EfPolicyStore(db), new RuleEngine(), TimeProvider.System);
        var head = await new EfPolicyStore(db).Head(default);
        var draft = await policies.CreateDraft(head.ActiveRevisionId, PolicyTags.Head(head), Guid.NewGuid(), default);
        var deniedRule = Policy.Rules[0] with { Conditions = [new("address.state", "equals", JsonSerializer.SerializeToElement("CA"))] };
        var changed = await policies.ReplaceRules([deniedRule], PolicyTags.Draft(draft.Revision), Guid.NewGuid(), default);
        var published = await policies.Publish(PolicyTags.Draft(changed), draft.Head.Version, Guid.NewGuid(), default);
        pause.Release.TrySetResult();
        Assert.Equal(head.ActiveRevisionId, (await pending).Approved!.PolicyRevisionId);
        Assert.Equal(published.ActiveRevisionId, (await Submit(Form)).Denied!.PolicyRevisionId);
    }
    [Fact]
    public async Task Exhausted_conflicts_are_bounded_and_leave_no_partial_rows()
    {
        await Seed(); var failures = new AlwaysConflicting();
        Assert.Equal("SUBMISSION_CONFLICT", (await Assert.ThrowsAsync<PolicyError>(() => Submit(Form, failures))).Code);
        Assert.Equal(3, failures.Attempts);
        await using var db = Open(); Assert.Empty(await db.Customers.ToListAsync()); Assert.Empty(await db.OutboxMessages.ToListAsync());
        Assert.Single(await db.AuditEvents.ToListAsync());
    }
    private sealed class AlwaysConflicting : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { Attempts++; throw new DbUpdateConcurrencyException("Injected known rollback conflict"); }
    }

    private sealed class Gate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously); private int count;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { if (Interlocked.Increment(ref count) == 2) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); return r; }
    }
    private sealed class Pause : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); return r; }
    }
    private sealed class InvalidRow(string target) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        {
            var db = e.Context!;
            if (target == "customer") db.ChangeTracker.Entries<Customer>().Single().Entity.FirstName = new string('x', 101);
            if (target == "application") db.ChangeTracker.Entries<LoanApplication>().Single().Entity.Currency = "BAD";
            if (target == "outbox") db.ChangeTracker.Entries<OutboxMessage>().Single().Entity.Status = "BAD";
            if (target == "audit") db.ChangeTracker.Entries<AuditEvent>().First().Entity.Action = new string('x', 81);
            return ValueTask.FromResult(r);
        }
    }
    private sealed class LostAck(bool afterSave) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default) => afterSave ? ValueTask.FromResult(r) : throw new TimeoutException();
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int r, CancellationToken ct = default) => throw new TimeoutException();
    }
}

// AC-033 / FR-018 — corrupt, unknown-schema and missing-active policy produce 503, not Denied.
public sealed class PolicyDocumentFaultTests : IsolatedPolicyDatabase
{
    private static Submission Form => new("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", 100m, "000000003");

    private async Task SubmitExpect503(string? documentJson)
    {
        if (documentJson is not null)
        {
            // Insert a Published revision via raw ADO.NET to avoid EF interpolated-SQL analyzer warnings.
            await using var setup = Open();
            await setup.Database.OpenConnectionAsync();
            var conn = (NpgsqlConnection)setup.Database.GetDbConnection();
            var revId = Guid.NewGuid();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "PolicyRevisions"
                    ("Id","Kind","SchemaVersion","DocumentJson","DraftVersion","CreatedAtUtc","PublishedAtUtc")
                VALUES
                    (@id,'Published',1,@doc::jsonb,1,clock_timestamp(),clock_timestamp());
                UPDATE "PolicyHeads"
                    SET "ActiveRevisionId"=@id, "Version"="Version"+1
                WHERE "Id"=1;
                """, conn);
            cmd.Parameters.AddWithValue("id", revId);
            cmd.Parameters.AddWithValue("doc", documentJson);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var db = Open();
        var service = new ApplicationService(
            new EfPolicyStore(db),
            new EfApplicationStore(Options(), TimeProvider.System),
            new RuleEngine());
        var error = await Assert.ThrowsAsync<PolicyError>(
            () => service.Submit(Form, Guid.NewGuid(), default));
        Assert.Equal(503, error.Status);
        await using var verify = Open();
        Assert.Empty(await verify.Customers.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Corrupt_json_in_active_policy_returns_503_without_business_rows()
        // Missing required fields makes the policy document structurally invalid at application level.
        => await SubmitExpect503("{\"schemaVersion\":1}");

    [Fact]
    public async Task Unknown_schema_version_in_active_policy_returns_503_without_business_rows()
        => await SubmitExpect503("{\"schemaVersion\":99,\"rules\":[],\"blacklist\":[]}");

    [Fact]
    public async Task Absent_policy_returns_503_and_no_business_rows()
        // No seed — policy head has no ActiveRevisionId.
        => await SubmitExpect503(null);

    [Fact]
    public async Task Explicit_empty_policy_approves_and_is_distinct_from_missing_policy()
    {
        // AC-043: a valid policy with rules=[] must approve, unlike the absent-policy case.
        await using var db = Open();
        var policies = new EfPolicyStore(db);
        await new PolicyService(policies, new RuleEngine(), TimeProvider.System)
            .Seed(new(1, [], []), Guid.NewGuid(), default);
        var service = new ApplicationService(
            policies, new EfApplicationStore(Options(), TimeProvider.System), new RuleEngine());
        var result = (await service.Submit(Form, Guid.NewGuid(), default)).Approved;
        Assert.NotNull(result);
        Assert.Equal("Approved", result.Decision);
        await using var verify = Open();
        Assert.Single(await verify.Customers.ToListAsync());
        Assert.Single(await verify.Applications.ToListAsync());
    }
}
