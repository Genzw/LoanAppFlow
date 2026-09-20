using LoanApp.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LoanApp.Api.Infrastructure.Delivery;

public sealed class OutboxStore(DbContextOptions<AppDbContext> options, IHttpContextAccessor? http = null)
{
    public async Task<double?> NextDueSeconds(CancellationToken ct)
    {
        await using var db = new AppDbContext(options);
        return await db.Database.SqlQuery<double?>($"""
            SELECT EXTRACT(epoch FROM (
                MIN(GREATEST(o."NextAttemptAtUtc", COALESCE(o."LeaseExpiresAtUtc", o."NextAttemptAtUtc"))) - clock_timestamp()
            ))::double precision AS "Value"
            FROM "OutboxMessages" o
            WHERE o."Status"='Pending' AND NOT EXISTS (
                SELECT 1 FROM "OutboxMessages" p WHERE p."ApplicationId"=o."ApplicationId"
                AND p."ApplicationVersion"<o."ApplicationVersion" AND p."Status"<>'Delivered')
            """).SingleAsync(ct);
    }
    public async Task Retry(Guid id, Guid correlation, CancellationToken ct)
    {
        await using var db = new AppDbContext(options); var audit = Guid.NewGuid();
        var actor = http?.HttpContext?.Items["DemoSessionId"] as Guid?;
        var actorType = actor.HasValue ? "DemoSession" : "Local";
        var actorRef = actor?.ToString();
        var counts = await db.Database.SqlQuery<int>($"""
            WITH changed AS (
              UPDATE "OutboxMessages" SET "Status"='Pending',"NextAttemptAtUtc"=clock_timestamp(),"LastErrorCode"=NULL,"LeaseToken"=NULL,"LeaseExpiresAtUtc"=NULL
              WHERE "Id"={id} AND "Status" IN ('Pending','Failed') AND ("LeaseExpiresAtUtc" IS NULL OR "LeaseExpiresAtUtc"<=clock_timestamp()) RETURNING *
            ), audited AS (
              INSERT INTO "AuditEvents" ("Id","Component","Action","CorrelationId","ActorType","ActorRef","EntityType","EntityId","Outcome","PolicyRevisionId","OutboxEventId","MetadataJson")
              SELECT {audit},'Api','Outbox.RetryRequested',{correlation},{actorType},{actorRef},'OutboxMessage',"Id",'Succeeded',"PolicyRevisionId","Id",
                jsonb_build_object('applicationVersion',"ApplicationVersion") FROM changed RETURNING "Id"
            ) SELECT count(*)::int AS "Value" FROM audited
            """).ToListAsync(ct);
        if (counts.Single() == 1) return;
        throw new LoanApp.Core.Application.PolicyError(await db.OutboxMessages.AnyAsync(o => o.Id == id, ct) ? 409 : 404, "EVENT_NOT_RETRYABLE");
    }
    public async Task<OutboxMessage?> Claim(CancellationToken ct)
    {
        await using var db = new AppDbContext(options); var token = Guid.NewGuid(); var audit = Guid.NewGuid();
        var rows = await db.OutboxMessages.FromSqlInterpolated($"""
            WITH candidate AS (
              SELECT o."Id" FROM "OutboxMessages" o
              WHERE o."Status" = 'Pending' AND o."NextAttemptAtUtc" <= clock_timestamp()
                AND (o."LeaseExpiresAtUtc" IS NULL OR o."LeaseExpiresAtUtc" <= clock_timestamp())
                AND NOT EXISTS (SELECT 1 FROM "OutboxMessages" p WHERE p."ApplicationId"=o."ApplicationId"
                  AND p."ApplicationVersion"<o."ApplicationVersion" AND p."Status"<>'Delivered')
              ORDER BY o."CreatedAtUtc", o."Id" LIMIT 1 FOR UPDATE OF o SKIP LOCKED
            ), claimed AS (
              UPDATE "OutboxMessages" o SET "LeaseToken"={token}, "LeaseExpiresAtUtc"=clock_timestamp()+interval '45 seconds'
              FROM candidate c WHERE o."Id"=c."Id" RETURNING o.*
            ), audited AS (
              INSERT INTO "AuditEvents" ("Id","Component","Action","CorrelationId","ActorType","EntityType","EntityId","Outcome","PolicyRevisionId","OutboxEventId","MetadataJson")
              SELECT {audit},'Api','Outbox.Claimed',"CorrelationId",'Worker','OutboxMessage',"Id",'Succeeded',"PolicyRevisionId","Id",
                jsonb_build_object('applicationVersion',"ApplicationVersion",'attemptCount',"AttemptCount") FROM claimed
            ) SELECT * FROM claimed
            """).AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault();
    }
    public async Task<bool> Complete(OutboxMessage message, DeliveryOutcome outcome, CancellationToken ct)
    {
        await using var db = new AppDbContext(options); var status = outcome.Kind switch { "Delivered" => "Delivered", "Failed" => "Failed", _ => "Pending" };
        var action = "Outbox." + (status == "Pending" ? "RetryScheduled" : status); var audit = Guid.NewGuid();
        var counts = await db.Database.SqlQuery<int>($"""
            WITH changed AS (
              UPDATE "OutboxMessages" SET "Status"={status},"AttemptCount"="AttemptCount"+1,
                "DeliveredAtUtc"=CASE WHEN {status}='Delivered' THEN clock_timestamp() ELSE NULL END,
                "NextAttemptAtUtc"=clock_timestamp()+make_interval(secs => CASE WHEN "AttemptCount"<5 THEN power(2,"AttemptCount") ELSE 30 END),
                "LastErrorCode"={outcome.Code},"LeaseToken"=NULL,"LeaseExpiresAtUtc"=NULL
              WHERE "Id"={message.Id} AND "Status"='Pending' AND "LeaseToken"={message.LeaseToken}
                AND "LeaseExpiresAtUtc">clock_timestamp() RETURNING *
            ), audited AS (
              INSERT INTO "AuditEvents" ("Id","Component","Action","CorrelationId","ActorType","EntityType","EntityId","Outcome","PolicyRevisionId","OutboxEventId","ReasonCode","MetadataJson")
              SELECT {audit},'Api',{action},"CorrelationId",'Worker','OutboxMessage',"Id",'Succeeded',"PolicyRevisionId","Id",{outcome.Code},
                jsonb_build_object('applicationVersion',"ApplicationVersion",'attemptCount',"AttemptCount") FROM changed RETURNING "Id"
            ) SELECT count(*)::int AS "Value" FROM audited
            """).ToListAsync(ct);
        return counts.Single() == 1;
    }
}
public sealed record DeliveryOutcome(string Kind, string? Code = null);
