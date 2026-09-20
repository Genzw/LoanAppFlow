using System.Text.Json;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LoanApp.IntegrationTests;
public sealed class AuditTests : IsolatedPolicyDatabase
{
    [Fact]
    public async Task Keyset_pages_handle_equal_timestamps_insertions_filters_and_safe_metadata()
    {
        await using var db = Open(); var time = DateTimeOffset.Parse("2026-09-18T00:00:00Z"); var correlation = Guid.NewGuid();
        for (var n = 1; n <= 3; n++) db.AuditEvents.Add(new() { Id = Guid.Parse($"00000000-0000-4000-8000-{n:000000000000}"), OccurredAtUtc = time,
            Component = "Api", Action = "Application.Created", ActorType = "Local", ActorRef = "do-not-expose", CorrelationId = correlation,
            EntityType = "Application", EntityId = correlation, Outcome = "Succeeded", MetadataJson = "{\"applicationVersion\":1,\"ssn\":\"000000003\",\"payload\":\"private\"}" });
        await db.SaveChangesAsync(); var reader = new AuditReader(db);
        var filters = new Dictionary<string, string> { ["limit"] = "1", ["correlationId"] = correlation.ToString(), ["fromUtc"] = time.ToString("O"), ["toUtc"] = time.AddSeconds(1).ToString("O"), ["action"] = "Application.Created", ["entityType"] = "Application", ["entityId"] = correlation.ToString() };
        var first = await reader.Read(AuditQuery.Parse(filters, "Api"), default);
        Assert.EndsWith("000000000003", Assert.Single(first.Items).Id.ToString()); Assert.NotNull(first.NextCursor);
        Assert.Null(first.Items[0].ActorRef); var json = JsonSerializer.Serialize(first); Assert.DoesNotContain("\"ssn\"", json); Assert.DoesNotContain("private", json); Assert.DoesNotContain("do-not-expose", json);
        Assert.Single(first.Items[0].Metadata); Assert.Equal(1, first.Items[0].Metadata["applicationVersion"].GetInt32());
        db.AuditEvents.Add(new() { Id = Guid.NewGuid(), OccurredAtUtc = time.AddMilliseconds(1), Component = "Api", Action = "Application.Created", ActorType = "Local", CorrelationId = correlation, EntityType = "Application", EntityId = correlation, Outcome = "Succeeded" });
        await db.SaveChangesAsync(); filters["cursor"] = first.NextCursor!;
        var second = await reader.Read(AuditQuery.Parse(filters, "Api"), default); Assert.EndsWith("000000000002", Assert.Single(second.Items).Id.ToString());
        filters["cursor"] = second.NextCursor!;
        var third = await reader.Read(AuditQuery.Parse(filters, "Api"), default); Assert.EndsWith("000000000001", Assert.Single(third.Items).Id.ToString()); Assert.Null(third.NextCursor);
        filters["action"] = "Application.Updated"; Assert.Equal(400, Assert.Throws<PolicyError>(() => AuditQuery.Parse(filters, "Api")).Status);
        filters["action"] = "Application.Created"; Assert.Throws<PolicyError>(() => AuditQuery.Parse(filters, "Mock"));
        filters.Remove("cursor"); filters["toUtc"] = time.ToString("O"); filters["fromUtc"] = time.AddSeconds(-1).ToString("O");
        Assert.Empty((await reader.Read(AuditQuery.Parse(filters, "Api"), default)).Items);
        Assert.Equal(4, await db.AuditEvents.CountAsync()); // Reads never create recursive audit entries.
    }
}
