using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoanApp.Api.Infrastructure.Delivery;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace LoanApp.IntegrationTests;

public sealed class OutboxTests : IsolatedPolicyDatabase
{
    private async Task SeedEvents()
    {
        await using var db = Open(); var policies = new EfPolicyStore(db);
        await new PolicyService(policies, new RuleEngine(), TimeProvider.System).Seed(new(1, [], []), Guid.NewGuid(), default);
        var service = new ApplicationService(policies, new EfApplicationStore(Options(), TimeProvider.System), new RuleEngine());
        var form = new Submission("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", 100, "000000003");
        await service.Submit(form, Guid.NewGuid(), default); await service.Submit(form with { RequestedAmount = 200 }, Guid.NewGuid(), default);
    }
    [Fact]
    public async Task Next_wakeup_uses_database_deadlines_and_ignores_blocked_successors()
    {
        var store = new OutboxStore(Options()); Assert.Null(await store.NextDueSeconds(default));
        await SeedEvents(); Assert.True(await store.NextDueSeconds(default) <= 0);
        await using var db = Open(); var first = await db.OutboxMessages.SingleAsync(o => o.ApplicationVersion == 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"NextAttemptAtUtc\"=clock_timestamp()+interval '60 seconds' WHERE \"Id\"={first.Id}");
        Assert.InRange((await store.NextDueSeconds(default))!.Value, 50, 61);
        // A live lease postpones recovery even when retry time is earlier.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"LeaseToken\"={Guid.NewGuid()},\"LeaseExpiresAtUtc\"=clock_timestamp()+interval '120 seconds' WHERE \"Id\"={first.Id}");
        Assert.InRange((await store.NextDueSeconds(default))!.Value, 110, 121);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"Status\"='Failed',\"LeaseToken\"=NULL,\"LeaseExpiresAtUtc\"=NULL WHERE \"Id\"={first.Id}");
        Assert.Null(await store.NextDueSeconds(default));
        await store.Retry(first.Id, Guid.NewGuid(), default); Assert.True(await store.NextDueSeconds(default) <= 0);
    }
    [Fact]
    public async Task Claim_is_exclusive_and_order_blocks_successors_until_delivery()
    {
        await SeedEvents(); var store = new OutboxStore(Options());
        var claims = await Task.WhenAll(store.Claim(default), store.Claim(default)); var first = Assert.Single(claims, c => c is not null)!;
        Assert.Equal(1, first.ApplicationVersion); Assert.Null(await store.Claim(default));
        Assert.True(await store.Complete(first, new("Delivered"), default));
        var second = (await store.Claim(default))!; Assert.Equal(2, second.ApplicationVersion);
        Assert.False(await store.Complete(first, new("Failed", "STALE"), default));
        Assert.True(await store.Complete(second, new("Delivered"), default));
        await using var db = Open(); Assert.Equal(2, await db.OutboxMessages.CountAsync(o => o.Status == "Delivered"));
        Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.Action == "Outbox.Delivered"));
    }
    [Fact]
    public async Task Expired_owner_cannot_record_result_and_new_claim_can_recover()
    {
        await SeedEvents(); var store = new OutboxStore(Options()); var first = (await store.Claim(default))!;
        await using var db = Open(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"LeaseExpiresAtUtc\"=clock_timestamp()-interval '1 second' WHERE \"Id\"={first.Id}");
        Assert.False(await store.Complete(first, new("Delivered"), default));
        var next = (await store.Claim(default))!; Assert.Equal(first.Id, next.Id); Assert.NotEqual(first.LeaseToken, next.LeaseToken);
        Assert.False(await store.Complete(first, new("Delivered"), default)); Assert.True(await store.Complete(next, new("Delivered"), default));
    }
    [Fact]
    public async Task Retry_backoff_and_permanent_failure_block_later_versions()
    {
        await SeedEvents(); var store = new OutboxStore(Options()); var first = (await store.Claim(default))!;
        Assert.True(await store.Complete(first, new("Retry", "HTTP_TIMEOUT"), default)); Assert.Null(await store.Claim(default));
        await using var db = Open(); var saved = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.Id == first.Id);
        Assert.Equal(1, saved.AttemptCount); Assert.Null(saved.LeaseToken); Assert.Equal("Pending", saved.Status);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"NextAttemptAtUtc\"=clock_timestamp()-interval '1 second' WHERE \"Id\"={first.Id}");
        var retry = (await store.Claim(default))!; Assert.Equal(first.PayloadJson, retry.PayloadJson);
        Assert.True(await store.Complete(retry, new("Failed", "HTTP_REJECTED"), default)); Assert.Null(await store.Claim(default));
        await store.Retry(first.Id, Guid.NewGuid(), default);
        var manual = (await store.Claim(default))!; Assert.Equal(first.Id, manual.Id); Assert.Equal(first.PayloadJson, manual.PayloadJson);
        Assert.Equal(409, (await Assert.ThrowsAsync<PolicyError>(() => store.Retry(first.Id, Guid.NewGuid(), default))).Status);
        Assert.True(await store.Complete(manual, new("Delivered"), default));
        Assert.Equal(409, (await Assert.ThrowsAsync<PolicyError>(() => store.Retry(first.Id, Guid.NewGuid(), default))).Status);
    }
    [Theory]
    [InlineData(200, "json", "Delivered")] [InlineData(200, "html", "Retry")] [InlineData(200, "wrong", "Failed")]
    [InlineData(200, "malformed", "Failed")] [InlineData(503, "json", "Retry")] [InlineData(429, "json", "Retry")] [InlineData(409, "json", "Failed")]
    public async Task Http_delivery_classifies_transport_and_checks_receipt(int status, string format, string expected)
    {
        await SeedEvents(); var message = (await new OutboxStore(Options()).Claim(default))!;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(message.CorrelationId.ToString(), request.Headers.GetValues("X-Correlation-Id").Single());
            var payload = (await request.Content!.ReadFromJsonAsync<ApplicationEvent>())!; Assert.Equal(message.Id, payload.EventId);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = format switch {
                "html" => new StringContent("<html>Starting</html>"), "malformed" => new StringContent("{", System.Text.Encoding.UTF8, "application/json"),
                _ => JsonContent.Create(new DeliveryReceipt(format == "wrong" ? Guid.NewGuid() : message.Id, message.ApplicationId, message.ApplicationVersion, false)) } };
        })) { BaseAddress = new("http://127.0.0.1:5200/") };
        Assert.Equal(expected, (await new DeliveryClient(http).Send(message, default)).Kind);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }

    [Fact]
    public async Task Failed_predecessor_does_not_block_an_unrelated_application()
    {
        await SeedEvents(); var store = new OutboxStore(Options()); var first = (await store.Claim(default))!;
        await store.Complete(first, new("Failed", "HTTP_REJECTED"), default);
        await using var db = Open();
        var service = new ApplicationService(new EfPolicyStore(db), new EfApplicationStore(Options(), TimeProvider.System), new RuleEngine());
        var other = (await service.Submit(new("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", 100, "000000006"), Guid.NewGuid(), default)).Approved!;
        var candidate = (await store.Claim(default))!; Assert.Equal(other.EventId, candidate.Id);
        Assert.NotEqual(first.ApplicationId, candidate.ApplicationId);
    }
    [Fact]
    public async Task Audit_failure_rolls_back_claim_and_delivery_transition()
    {
        await SeedEvents(); var broken = new OutboxStore(Options(new RejectAuditCommand()));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => broken.Claim(default));
        await using var db = Open(); Assert.All(await db.OutboxMessages.AsNoTracking().ToListAsync(), row => Assert.Null(row.LeaseToken));
        var store = new OutboxStore(Options()); var claim = (await store.Claim(default))!;
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => broken.Complete(claim, new("Delivered"), default));
        var unchanged = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.Id == claim.Id);
        Assert.Equal("Pending", unchanged.Status); Assert.Equal(claim.LeaseToken, unchanged.LeaseToken); Assert.Equal(0, unchanged.AttemptCount);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Action == "Outbox.Delivered"));
    }
    private sealed class RejectAuditCommand : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            command.CommandText = command.CommandText.Replace("'Outbox.Claimed'", "repeat('x',81)");
            foreach (DbParameter parameter in command.Parameters) if (parameter.Value is "Outbox.Delivered") parameter.Value = new string('x', 81);
            return ValueTask.FromResult(result);
        }
    }
}

// AC-016 — Pending outbox events survive process restart (simulated by creating
// a fresh store instance without any in-memory state).
public sealed class OutboxRestartTests : IsolatedPolicyDatabase
{
    [Fact]
    public async Task Pending_events_survive_context_recreation_and_are_claimed_by_new_store()
    {
        // Seed an approved application so there is a Pending outbox row.
        await using var seedDb = Open();
        await new PolicyService(new EfPolicyStore(seedDb), new RuleEngine(), TimeProvider.System)
            .Seed(new(1, [], []), Guid.NewGuid(), default);
        var form = new Submission("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", 100, "000000003");
        await new ApplicationService(new EfPolicyStore(seedDb), new EfApplicationStore(Options(), TimeProvider.System), new RuleEngine())
            .Submit(form, Guid.NewGuid(), default);

        // Confirm the row is Pending.
        await using var check = Open();
        var before = await check.OutboxMessages.SingleAsync();
        Assert.Equal("Pending", before.Status);
        Assert.Null(before.LeaseToken);

        // Simulate restart: entirely new store instance, no shared in-memory state.
        var freshStore = new OutboxStore(Options());
        var claimed = await freshStore.Claim(default);
        Assert.NotNull(claimed);
        Assert.Equal(before.Id, claimed.Id);
        Assert.Equal(before.PayloadJson, claimed.PayloadJson);

        // Mark delivered via the fresh store — the old context cannot override.
        Assert.True(await freshStore.Complete(claimed, new("Delivered"), default));
        await using var final = Open();
        Assert.Equal("Delivered", (await final.OutboxMessages.SingleAsync()).Status);
    }
}
