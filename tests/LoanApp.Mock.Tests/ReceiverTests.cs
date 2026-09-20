using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Mock.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LoanApp.Mock.Tests;

public sealed class ReceiverTests : IsolatedMockDatabase
{
    private static ApplicationEvent Event()
    {
        var customer = Guid.NewGuid(); return new(1, Guid.NewGuid(), "Created", 1, Guid.NewGuid(),
            new(customer, "Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", "000000003"), new(Guid.NewGuid(), customer, 100, "USD"));
    }
    private Task<DeliveryReceipt> Receive(ApplicationEvent e, params IInterceptor[] interceptors) =>
        new EventReceiver(Options(interceptors)).Receive(e, e.Operation == "Created", e.Operation == "Created" ? null : e.Application.Id, Guid.NewGuid(), default);
    [Fact]
    public async Task Repeated_old_event_preserves_newer_resource_and_does_not_repeat_audit()
    {
        var first = Event(); Assert.False((await Receive(first)).Duplicate);
        var second = first with { EventId = Guid.NewGuid(), Operation = "Updated", ApplicationVersion = 2, Application = first.Application with { RequestedAmount = 200 } };
        await Receive(second);
        var replay = await Receive(first with { Customer = first.Customer with { Ssn = "000-00-0003", FirstName = " Ana " }, Application = first.Application with { RequestedAmount = 100.00m } });
        Assert.True(replay.Duplicate); Assert.Equal(1, replay.ApplicationVersion);
        await using var db = Open(); Assert.Equal(2, (await db.Applications.SingleAsync()).Version);
        Assert.Equal(2, await db.InboxReceipts.CountAsync()); Assert.Equal(2, await db.AuditEvents.CountAsync());
        var reused = await Assert.ThrowsAsync<PolicyError>(() => Receive(first with { Application = first.Application with { RequestedAmount = 999 } }));
        Assert.Equal(409, reused.Status);
    }
    [Fact]
    public async Task Concurrent_identical_event_applies_once()
    {
        var e = Event(); var gate = new Gate();
        var results = await Task.WhenAll(Receive(e, gate), Receive(e, gate));
        Assert.Single(results, r => r.Duplicate); await using var db = Open();
        Assert.Single(await db.Applications.ToListAsync()); Assert.Single(await db.InboxReceipts.ToListAsync()); Assert.Single(await db.AuditEvents.ToListAsync());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Audit_sql_failure_rolls_back_resource_and_receipt(bool update)
    {
        var e = Event(); if (update) { await Receive(e); e = e with { EventId = Guid.NewGuid(), ApplicationVersion = 2, Operation = "Updated" }; }
        await Assert.ThrowsAsync<DbUpdateException>(() => Receive(e, new RejectAudit()));
        await using var db = Open(); Assert.Equal(update ? 1 : 0, await db.InboxReceipts.CountAsync()); Assert.Equal(update ? 1 : 0, await db.AuditEvents.CountAsync());
        if (update) Assert.Equal(1, (await db.Applications.SingleAsync()).Version); else Assert.Empty(await db.Applications.ToListAsync());
    }
    [Fact]
    public async Task Missing_update_and_skipped_version_are_rejected()
    {
        var first = Event(); var update = first with { Operation = "Updated", ApplicationVersion = 2, EventId = Guid.NewGuid() };
        Assert.Equal(404, (await Assert.ThrowsAsync<PolicyError>(() => Receive(update))).Status);
        await Receive(first);
        Assert.Equal(409, (await Assert.ThrowsAsync<PolicyError>(() => Receive(update with { ApplicationVersion = 3 }))).Status);
        await using var db = Open(); Assert.Single(await db.InboxReceipts.ToListAsync());
    }
    [Fact]
    public async Task Http_post_put_and_masked_diagnostics_match_contract()
    {
        using var factory = new Factory(Options()); using var client = factory.CreateClient(); var e = Event();
        var created = await client.PostAsJsonAsync("/applications", e); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.False((await created.Content.ReadFromJsonAsync<DeliveryReceipt>())!.Duplicate);
        var update = e with { EventId = Guid.NewGuid(), Operation = "Updated", ApplicationVersion = 2 };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/applications/{e.Application.Id}", update)).StatusCode);
        Assert.True((await (await client.PostAsJsonAsync("/applications", e)).Content.ReadFromJsonAsync<DeliveryReceipt>())!.Duplicate);
        var view = await client.GetStringAsync("/admin/applications?limit=1"); Assert.Contains("***-**-0003", view); Assert.DoesNotContain("000000003", view); Assert.DoesNotContain("snapshotJson", view);
        var receipts = await client.GetFromJsonAsync<JsonElement>("/admin/receipts?limit=1"); Assert.Equal(2, receipts.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/receipts?cursor=bad")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:3000");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/applications", e)).StatusCode);
    }
    private sealed class Factory(DbContextOptions<MockDbContext> options) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Development"); b.UseSetting("ConnectionStrings:MockDb", Environment.GetEnvironmentVariable("TEST_MOCK_DB"));
            b.ConfigureServices(s => { s.RemoveAll<DbContextOptions<MockDbContext>>(); s.AddSingleton(options); s.AddTransient<IStartupFilter, Loopback>(); });
        }
    }
    private sealed class Loopback : IStartupFilter
    { public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => { app.Use((c, n) => { c.Connection.RemoteIpAddress = IPAddress.Loopback; return n(c); }); next(app); }; }
    private sealed class Gate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously); private int count;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { if (Interlocked.Increment(ref count) == 2) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); return r; }
    }
    private sealed class RejectAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { e.Context!.ChangeTracker.Entries<MockAuditEvent>().Single().Entity.Action = new string('x', 81); return ValueTask.FromResult(r); }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Lost_commit_acknowledgement_is_recovered_without_duplicate_effect(bool committed)
    {
        var e = Event();
        if (committed) Assert.True((await Receive(e, new LostAck(committed))).Duplicate);
        else Assert.Equal("OUTCOME_UNKNOWN", (await Assert.ThrowsAsync<PolicyError>(() => Receive(e, new LostAck(committed)))).Code);
        await using var db = Open(); Assert.Equal(committed ? 1 : 0, await db.Applications.CountAsync());
        Assert.Equal(committed ? 1 : 0, await db.InboxReceipts.CountAsync()); Assert.Equal(committed ? 1 : 0, await db.AuditEvents.CountAsync());
    }
    private sealed class LostAck(bool afterSave) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default) =>
            afterSave ? ValueTask.FromResult(r) : throw new TimeoutException();
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int r, CancellationToken ct = default) => throw new TimeoutException();
    }
}
