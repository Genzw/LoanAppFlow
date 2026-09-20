using LoanApp.Core.Application;
using LoanApp.Mock.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LoanApp.Mock.Tests;
public sealed class AuditTests : IsolatedMockDatabase
{
    [Fact]
    public async Task External_audit_filters_correlation_and_keeps_reads_side_effect_free()
    {
        var customer = Guid.NewGuid(); var correlation = Guid.NewGuid();
        var payload = new ApplicationEvent(1, Guid.NewGuid(), "Created", 1, Guid.NewGuid(), new(customer, "Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", "000000003"), new(Guid.NewGuid(), customer, 100, "USD"));
        var receiver = new EventReceiver(Options()); await receiver.Receive(payload, true, null, correlation, default); await receiver.Receive(payload, true, null, correlation, default);
        await using var db = Open(); var reader = new AuditReader(db);
        var page = await reader.Read(AuditQuery.Parse(new Dictionary<string, string> { ["correlationId"] = correlation.ToString() }, "Mock"), default);
        var item = Assert.Single(page.Items); Assert.Equal("Worker", item.ActorType); Assert.Equal(payload.EventId, item.OutboxEventId); Assert.Equal("ExternalApplication.Created", item.Action);
        Assert.Equal(1, item.Metadata["applicationVersion"].GetInt64()); Assert.Null(page.NextCursor);
        Assert.Empty((await reader.Read(AuditQuery.Parse(new Dictionary<string, string> { ["action"] = "Other" }, "Mock"), default)).Items);
        Assert.Equal(1, await db.AuditEvents.CountAsync());
    }
}
