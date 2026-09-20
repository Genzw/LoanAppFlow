using System.Net;
using System.Text;
using System.Text.Json;
using LoanApp.Api.Infrastructure.Delivery;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LoanApp.IntegrationTests;

public sealed class DiagnosticsTests : IsolatedPolicyDatabase
{
    [Fact]
    public async Task Pages_keep_global_counts_and_timeline_hides_payload_and_lease()
    {
        await using var db = Open(); var policies = new EfPolicyStore(db);
        await new PolicyService(policies, new RuleEngine(), TimeProvider.System).Seed(new(1, [], []), Guid.NewGuid(), default);
        var service = new ApplicationService(policies, new EfApplicationStore(Options(), TimeProvider.System), new RuleEngine());
        var form = new Submission("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", 100, "000000003");
        var first = (await service.Submit(form, Guid.NewGuid(), default)).Approved!;
        await service.Submit(form with { RequestedAmount = 200 }, Guid.NewGuid(), default);
        await service.Submit(form with { Ssn = "000000004" }, Guid.NewGuid(), default);
        var store = new DiagnosticsStore(db);
        var page = await store.List(1, null, default); Assert.Equal(2, page.CustomerCount); Assert.Equal(2, page.ApplicationCount);
        Assert.Single(page.Items); Assert.NotNull(page.NextCursor);
        var second = await store.List(1, new Guid(Convert.FromBase64String(page.NextCursor)), default);
        Assert.Single(second.Items); Assert.Equal(2, second.CustomerCount); Assert.Null(second.NextCursor);
        Assert.NotEqual(page.Items[0].ApplicationId, second.Items[0].ApplicationId);
        var detail = await store.Detail(first.ApplicationId, 1, null, default);
        Assert.Equal("***-**-0003", detail.Customer.MaskedSsn); Assert.Equal(2, detail.Application.Version);
        var latest = Assert.Single(detail.Events.Items); Assert.Equal(first.EventId, latest.BlockedByEventId); Assert.Equal(1, latest.BlockedByVersion);
        Assert.Equal("2", detail.Events.NextCursor);
        var old = await store.Detail(first.ApplicationId, 1, 2, default); Assert.Equal(first.EventId, Assert.Single(old.Events.Items).EventId); Assert.Null(old.Events.NextCursor);
        var json = JsonSerializer.Serialize(detail); Assert.DoesNotContain(form.Ssn, json); Assert.DoesNotContain("PayloadJson", json); Assert.DoesNotContain("LeaseToken", json);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"LeaseToken\"={Guid.NewGuid()}, \"LeaseExpiresAtUtc\"=clock_timestamp()+interval '45 seconds' WHERE \"Id\"={first.EventId}");
        Assert.True((await store.Detail(first.ApplicationId, 1, 2, default)).Events.Items[0].InProgress);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxMessages\" SET \"LeaseExpiresAtUtc\"=clock_timestamp()-interval '1 second' WHERE \"Id\"={first.EventId}");
        Assert.False((await store.Detail(first.ApplicationId, 1, 2, default)).Events.Items[0].InProgress);
        Assert.Equal(404, (await Assert.ThrowsAsync<PolicyError>(() => store.Detail(Guid.NewGuid(), 20, null, default))).Status);
    }
    [Fact]
    public async Task Empty_database_returns_zero_counts()
    {
        await using var db = Open(); var page = await new DiagnosticsStore(db).List(20, null, default);
        Assert.Empty(page.Items); Assert.Equal(0, page.CustomerCount); Assert.Equal(0, page.ApplicationCount); Assert.Null(page.NextCursor);
    }
}

public sealed class ExternalReaderTests
{
    [Theory]
    [InlineData("html")] [InlineData("invalid")] [InlineData("oversize")] [InlineData("sleeping")] [InlineData("unmasked")]
    public async Task Unavailable_or_invalid_external_response_never_becomes_success(string mode)
    {
        var json = mode switch
        {
            "oversize" => new string('x', 262145),
            "unmasked" => JsonSerializer.Serialize(new { items = new[] { new { applicationId = Guid.NewGuid(), customerId = Guid.NewGuid(), version = 1, updatedAtUtc = DateTimeOffset.UtcNow, companyName = "Demo", state = "CA", requestedAmount = 1, maskedSsn = "000000003" } }, totalCount = 1, nextCursor = (string?)null }),
            _ => "<html>Starting</html>"
        };
        using var client = new HttpClient(new Handler(new HttpResponseMessage(mode == "sleeping" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, mode == "html" ? "text/html" : "application/json") })) { BaseAddress = new("http://127.0.0.1:5200/") };
        var error = await Assert.ThrowsAsync<PolicyError>(() => new ExternalReader(client).Read<ExternalApplication>("applications", 20, null, Guid.NewGuid(), default));
        Assert.Equal(503, error.Status); Assert.Equal("EXTERNAL_UNAVAILABLE", error.Code);
    }
    [Fact]
    public async Task Receipt_projection_drops_unknown_fields_and_propagates_correlation()
    {
        var id = Guid.NewGuid(); var correlation = Guid.NewGuid();
        var handler = new Handler(new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { items = new[] { new { eventId = id, applicationId = Guid.NewGuid(), applicationVersion = 1, operation = "Created", receivedAtUtc = DateTimeOffset.UtcNow, payloadJson = "secret" } }, totalCount = 1, nextCursor = (string?)null }), Encoding.UTF8, "application/json") });
        using var client = new HttpClient(handler) { BaseAddress = new("http://127.0.0.1:5200/") };
        var page = await new ExternalReader(client).Read<ExternalReceipt>("receipts", 20, null, correlation, default);
        Assert.Equal(id, Assert.Single(page.Items).EventId); Assert.DoesNotContain("secret", JsonSerializer.Serialize(page));
        Assert.Equal(correlation.ToString(), handler.Correlation); Assert.Equal("/admin/receipts?limit=20", handler.Path);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Audit_projection_filters_metadata_and_rejects_invalid_origin(bool invalid)
    {
        var item = new AuditItem(Guid.NewGuid(), DateTimeOffset.UtcNow, invalid ? "Api" : "Mock", "ExternalApplication.Created", Guid.NewGuid(),
            "Worker", "secret", "ExternalApplication", Guid.NewGuid(), "Succeeded", "secret", Guid.NewGuid(), Guid.NewGuid(),
            new() { ["ssn"] = JsonSerializer.SerializeToElement("000000003"), ["applicationVersion"] = JsonSerializer.SerializeToElement(1) });
        using var client = new HttpClient(new Handler(new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new AuditPage([item], null), new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") })) { BaseAddress = new("http://127.0.0.1:5200/") };
        var reader = new ExternalReader(client);
        if (invalid) Assert.Equal(503, (await Assert.ThrowsAsync<PolicyError>(() => reader.ReadAudit(new Dictionary<string, string>(), 20, Guid.NewGuid(), default))).Status);
        else
        {
            var result = await reader.ReadAudit(new Dictionary<string, string>(), 20, Guid.NewGuid(), default);
            var safe = Assert.Single(result.Items); Assert.Null(safe.ActorRef); Assert.Null(safe.ReasonCode); Assert.Single(safe.Metadata);
            Assert.DoesNotContain("000000003", JsonSerializer.Serialize(result));
        }
    }
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Correlation { get; private set; } public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Correlation = request.Headers.GetValues("X-Correlation-Id").Single(); Path = request.RequestUri!.PathAndQuery; return Task.FromResult(response); }
    }
}
