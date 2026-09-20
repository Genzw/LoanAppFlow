using System.Security.Cryptography;
using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LoanApp.Mock.Persistence;

public sealed class EventReceiver(DbContextOptions<MockDbContext> options)
{
    public async Task<DeliveryReceipt> Receive(ApplicationEvent input, bool create, Guid? routeId, Guid correlation, CancellationToken ct)
    {
        var payload = EventContract.Normalize(input);
        if (create != (payload.Operation == "Created") || !create && routeId != payload.Application.Id)
            throw new PolicyError(422, "EVENT_ROUTE_MISMATCH");
        var json = JsonSerializer.Serialize(payload, PolicyJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = new MockDbContext(options);
            var receipt = await db.InboxReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.EventId == payload.EventId, ct);
            if (receipt is not null) return Duplicate(receipt, hash);
            var existing = await db.Applications.SingleOrDefaultAsync(a => a.ApplicationId == payload.Application.Id, ct);
            if (create && existing is not null || !create && (existing is null || existing.CustomerId != payload.Customer.Id || existing.Version + 1 != payload.ApplicationVersion))
            {
                var concurrent = await db.InboxReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.EventId == payload.EventId, ct);
                if (concurrent is not null) return Duplicate(concurrent, hash);
                throw new PolicyError(existing is null ? 404 : 409, existing is null ? "APPLICATION_NOT_FOUND" : "APPLICATION_VERSION_CONFLICT");
            }
            var resource = existing ?? new ExternalApplication { ApplicationId = payload.Application.Id, CustomerId = payload.Customer.Id, SnapshotJson = json };
            resource.Version = payload.ApplicationVersion; resource.SnapshotJson = json; resource.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (existing is null) db.Applications.Add(resource);
            db.InboxReceipts.Add(new() { EventId = payload.EventId, ApplicationId = payload.Application.Id, ApplicationVersion = payload.ApplicationVersion,
                Operation = payload.Operation, PayloadHash = hash, ReceivedAtUtc = DateTimeOffset.UtcNow });
            db.AuditEvents.Add(new() { Id = Guid.NewGuid(), Component = "Mock", Action = "ExternalApplication." + payload.Operation,
                ActorType = "Worker", EntityType = "ExternalApplication", EntityId = payload.Application.Id, CorrelationId = correlation,
                OutboxEventId = payload.EventId, PolicyRevisionId = payload.PolicyRevisionId, Outcome = "Succeeded",
                MetadataJson = JsonSerializer.Serialize(new { applicationVersion = payload.ApplicationVersion, customerId = payload.Customer.Id }) });
            try { await db.SaveChangesAsync(ct); return new(payload.EventId, payload.Application.Id, payload.ApplicationVersion, false); }
            catch (Exception error) when (error is DbUpdateConcurrencyException ||
                (error is DbUpdateException update ? update.InnerException : error) is PostgresException { SqlState: "23505" or "40001" or "40P01" })
            {
                // Re-read the winner's receipt before checking resource version again.
                if (attempt == 2)
                {
                    await using var recovery = new MockDbContext(options);
                    var winner = await recovery.InboxReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.EventId == payload.EventId, ct);
                    if (winner is not null) return Duplicate(winner, hash);
                    throw new PolicyError(409, "EVENT_CONFLICT");
                }
            }
            catch (Exception error) when (error is TimeoutException or NpgsqlException and not PostgresException || error is DbUpdateException { InnerException: NpgsqlException and not PostgresException })
            {
                try
                {
                    await using var recovery = new MockDbContext(options);
                    var committed = await recovery.InboxReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.EventId == payload.EventId, ct);
                    if (committed is not null) return Duplicate(committed, hash);
                }
                catch (Exception recoveryError) when (recoveryError is not OperationCanceledException and not PolicyError) { }
                throw new PolicyError(503, "OUTCOME_UNKNOWN");
            }
        }
        throw new PolicyError(409, "EVENT_CONFLICT");
    }
    private static DeliveryReceipt Duplicate(InboxReceipt receipt, string hash) => receipt.PayloadHash == hash
        ? new(receipt.EventId, receipt.ApplicationId, receipt.ApplicationVersion, true) : throw new PolicyError(409, "EVENT_CONTENT_CONFLICT");
}
