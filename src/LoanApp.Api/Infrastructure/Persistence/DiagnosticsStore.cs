using System.Data;
using LoanApp.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace LoanApp.Api.Infrastructure.Persistence;

public sealed record DiagnosticPage<T>(T[] Items, string? NextCursor);
public sealed class DiagnosticCounts { public long CustomerCount { get; set; } public long ApplicationCount { get; set; } }
public sealed record ApplicationSummary(Guid ApplicationId, Guid CustomerId, decimal RequestedAmount, long Version, Guid PolicyRevisionId, string? Status);
public sealed record ApplicationList(ApplicationSummary[] Items, string? NextCursor, long CustomerCount, long ApplicationCount);
public sealed record CustomerSummary(Guid Id, string FirstName, string LastName, string CompanyName, string State, string MaskedSsn);
public sealed record ApplicationDetail(ApplicationSummary Application, CustomerSummary Customer, DiagnosticPage<EventSummary> Events);
public sealed class EventSummary
{
    public Guid EventId { get; set; }
    public long ApplicationVersion { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public string Operation { get; set; } = "";
    public string Status { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public bool InProgress { get; set; }
    public Guid? BlockedByEventId { get; set; }
    public long? BlockedByVersion { get; set; }
}
public sealed class DiagnosticsStore(AppDbContext db)
{
    public async Task<ApplicationList> List(int limit, Guid? cursor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var counts = await db.Database.SqlQuery<DiagnosticCounts>($"""
            SELECT (SELECT count(*) FROM "Customers") AS "CustomerCount",
                   (SELECT count(*) FROM "Applications") AS "ApplicationCount"
            """).SingleAsync(ct);
        var query = db.Applications.AsNoTracking().AsQueryable();
        if (cursor is { } id) query = query.Where(a => a.Id.CompareTo(id) > 0);
        var rows = await query.OrderBy(a => a.Id).Take(limit + 1).Select(a => new ApplicationSummary(a.Id, a.CustomerId,
            a.RequestedAmount, a.Version, a.LastPolicyRevisionId,
            db.OutboxMessages.Where(o => o.ApplicationId == a.Id && o.ApplicationVersion == a.Version).Select(o => o.Status).FirstOrDefault())).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        var items = rows.Take(limit).ToArray();
        return new(items, rows.Length > limit ? Convert.ToBase64String(items[^1].ApplicationId.ToByteArray()) : null, counts.CustomerCount, counts.ApplicationCount);
    }
    public async Task<ApplicationDetail> Detail(Guid id, int limit, long? beforeVersion, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var row = await (from application in db.Applications.AsNoTracking() join customer in db.Customers on application.CustomerId equals customer.Id
            where application.Id == id select new { Application = application, Customer = customer }).SingleOrDefaultAsync(ct)
            ?? throw new PolicyError(404, "APPLICATION_NOT_FOUND");
        var events = db.Database.SqlQuery<EventSummary>($"""
            SELECT o."Id" AS "EventId", o."ApplicationVersion", o."PolicyRevisionId", o."Operation", o."Status",
              o."AttemptCount", o."CreatedAtUtc", o."NextAttemptAtUtc", o."DeliveredAtUtc", o."LastErrorCode",
              (o."Status" = 'Pending' AND o."LeaseToken" IS NOT NULL AND o."LeaseExpiresAtUtc" > statement_timestamp()) AS "InProgress",
              (SELECT p."Id" FROM "OutboxMessages" p WHERE p."ApplicationId"=o."ApplicationId" AND p."ApplicationVersion"<o."ApplicationVersion" AND p."Status"<>'Delivered' ORDER BY p."ApplicationVersion" LIMIT 1) AS "BlockedByEventId",
              (SELECT p."ApplicationVersion" FROM "OutboxMessages" p WHERE p."ApplicationId"=o."ApplicationId" AND p."ApplicationVersion"<o."ApplicationVersion" AND p."Status"<>'Delivered' ORDER BY p."ApplicationVersion" LIMIT 1) AS "BlockedByVersion"
            FROM "OutboxMessages" o WHERE o."ApplicationId"={id}
            """);
        if (beforeVersion is { } version) events = events.Where(e => e.ApplicationVersion < version);
        var rows = await events.OrderByDescending(e => e.ApplicationVersion).Take(limit + 1).ToArrayAsync(ct);
        var a = row.Application; var c = row.Customer;
        var status = await db.OutboxMessages.Where(o => o.ApplicationId == id && o.ApplicationVersion == a.Version).Select(o => o.Status).SingleOrDefaultAsync(ct);
        await tx.CommitAsync(ct);
        var selected = rows.Take(limit).ToArray();
        return new(new(a.Id, a.CustomerId, a.RequestedAmount, a.Version, a.LastPolicyRevisionId, status),
            new(c.Id, c.FirstName, c.LastName, c.CompanyName, c.State, "***-**-" + c.Ssn[^4..]),
            new(selected, rows.Length > limit ? selected[^1].ApplicationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }
}
