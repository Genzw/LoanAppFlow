using LoanApp.Core.Application;
using Microsoft.EntityFrameworkCore;
namespace LoanApp.Api.Infrastructure.Persistence;
public sealed class AuditReader(AppDbContext db)
{
    public async Task<AuditPage> Read(AuditQuery query, CancellationToken ct) => query.Page(await query.Apply(
        db.Database.SqlQueryRaw<AuditRow>("""
            SELECT "Id", "OccurredAtUtc", "Component", "Action", "CorrelationId", "ActorType", "ActorRef",
                   "EntityType", "EntityId", "Outcome", "ReasonCode", "PolicyRevisionId", "OutboxEventId", "MetadataJson"
            FROM "AuditEvents"
            """)).ToArrayAsync(ct));
}
