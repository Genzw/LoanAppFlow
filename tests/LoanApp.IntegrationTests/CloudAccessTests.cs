using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Api.Infrastructure.Delivery;
using System.Data.Common;
using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Core.Rules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
namespace LoanApp.IntegrationTests;
public sealed class CloudAccessTests : IsolatedPolicyDatabase
{
    [Fact]
    public async Task Cloud_http_rejects_bypass_and_records_authenticated_actor_in_both_audit_paths()
    {
        await using var db = Open(); await new PolicyService(new EfPolicyStore(db), new RuleEngine(), TimeProvider.System).Seed(new(1, [], []), Guid.NewGuid(), default);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var actor = Guid.NewGuid();
        await using var factory = new Factory(this, token); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/audit")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Demo-Session", actor.ToString()); client.DefaultRequestHeaders.Add("X-Backend-Token", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/audit")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Backend-Token"); client.DefaultRequestHeaders.Add("X-Backend-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/rule-catalog")).StatusCode);
        var form = new { firstName = "Ana", lastName = "Paz", companyName = "Demo", requestedAmount = 100, ssn = "000000003", address = new { line1 = "Demo", line2 = (string?)null, city = "Demo", state = "CA", postalCode = "90001" } };
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/applications", form)).StatusCode);
        Assert.Empty(await db.Applications.ToArrayAsync());
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://demo.example");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/applications", form)).StatusCode);
        var audit = await db.AuditEvents.Where(a => a.Action.StartsWith("Application.") || a.Action.StartsWith("Customer.") || a.Action == "Outbox.Created").ToArrayAsync();
        Assert.Equal(4, audit.Length); Assert.All(audit, a => { Assert.Equal("DemoSession", a.ActorType); Assert.Equal(actor.ToString(), a.ActorRef); });
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/admin/outbox/{message.Id}/retry", null)).StatusCode);
        var retry = await db.AuditEvents.SingleAsync(a => a.Action == "Outbox.RetryRequested"); Assert.Equal("DemoSession", retry.ActorType); Assert.Equal(actor.ToString(), retry.ActorRef);
    }
    [Fact]
    public async Task Cloud_worker_is_woken_after_commit_even_when_idle_delay_is_ten_minutes()
    {
        await using var db = Open(); await new PolicyService(new EfPolicyStore(db), new RuleEngine(), TimeProvider.System).Seed(new(1, [], []), Guid.NewGuid(), default);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var observer = new ScheduleObserver();
        await using var factory = new Factory(this, token, observer); using var client = factory.CreateClient();
        await observer.Queried.Task.WaitAsync(TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.Add("X-Backend-Token", token); client.DefaultRequestHeaders.Add("X-Demo-Session", Guid.NewGuid().ToString()); client.DefaultRequestHeaders.Add("Origin", "https://demo.example");
        var form = new { firstName = "Ana", lastName = "Paz", companyName = "Demo", requestedAmount = 100, ssn = "000000003", address = new { line1 = "Demo", line2 = (string?)null, city = "Demo", state = "CA", postalCode = "90001" } };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/applications", form)).StatusCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await db.OutboxMessages.AnyAsync(o => o.Status == "Delivered", deadline.Token)) await Task.Delay(50, deadline.Token);
        Assert.True(factory.Delivery.Authenticated); Assert.Equal(1, factory.Delivery.Calls);
        var delivered = await db.AuditEvents.SingleAsync(a => a.Action == "Outbox.Delivered"); Assert.Equal("Worker", delivered.ActorType); Assert.Null(delivered.ActorRef);
    }
    private sealed class ScheduleObserver : DbCommandInterceptor
    {
        public TaskCompletionSource Queried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        { if (command.CommandText.Contains("MIN(GREATEST", StringComparison.Ordinal)) Queried.TrySetResult(); return ValueTask.FromResult(result); }
    }
    private sealed class ReceiptHandler(string token) : HttpMessageHandler
    {
        public int Calls { get; private set; } public bool Authenticated { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Authenticated = request.Headers.TryGetValues("X-Integration-Token", out var values) && values.Single() == token && request.RequestUri!.Scheme == "https";
            var payload = JsonSerializer.Deserialize<ApplicationEvent>(await request.Content!.ReadAsStringAsync(ct), PolicyJson.Options)!;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DeliveryReceipt(payload.EventId, payload.Application.Id, payload.ApplicationVersion, false)) };
        }
    }
    private sealed class Factory(CloudAccessTests owner, string token, ScheduleObserver? observer = null) : WebApplicationFactory<Program>
    {
        private readonly string integrationToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public ReceiptHandler Delivery { get; private set; } = null!;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); builder.UseSetting("App:Profile", "CloudDemo"); builder.UseSetting("Outbox:Enabled", observer is null ? "false" : "true");
            builder.UseSetting("ConnectionStrings:AppDb", Environment.GetEnvironmentVariable("TEST_APP_DB"));
            builder.UseSetting("Security:BackendToken", token); builder.UseSetting("Security:DemoOrigin", "https://demo.example");
            builder.UseSetting("ExternalService:BaseUrl", "https://mock.example"); builder.UseSetting("ExternalService:Token", integrationToken);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddScoped(sp => owner.Options(observer is null ? [new DemoAuditInterceptor(sp.GetRequiredService<IHttpContextAccessor>())] : [new DemoAuditInterceptor(sp.GetRequiredService<IHttpContextAccessor>()), observer]));
                if (observer is not null)
                {
                    var cadence = new WorkerCadence(true); cadence.Next(null); cadence.Next(null);
                    services.RemoveAll<WorkerCadence>(); services.AddSingleton(cadence);
                    Delivery = new ReceiptHandler(integrationToken);
                    services.AddHttpClient<DeliveryClient>().ConfigurePrimaryHttpMessageHandler(() => Delivery);
                }
            });
        }
    }
}
