using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using LoanApp.Mock.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LoanApp.Mock;

public static class Endpoints
{
    public static void MapReceiver(this WebApplication app)
    {
        app.MapPost("/applications", async (HttpContext http, EventReceiver receiver, CancellationToken ct) =>
            Results.Ok(await receiver.Receive(await Body(http, ct), true, null, (Guid)http.Items["CorrelationId"]!, ct)));
        app.MapPut("/applications/{id:guid}", async (Guid id, HttpContext http, EventReceiver receiver, CancellationToken ct) =>
            Results.Ok(await receiver.Receive(await Body(http, ct), false, id, (Guid)http.Items["CorrelationId"]!, ct)));
        app.MapGet("/admin/applications", async (HttpContext http, MockDbContext db, CancellationToken ct) =>
        {
            var (limit, cursor) = Page(http); var query = db.Applications.AsNoTracking();
            var totalCount = await query.CountAsync(ct);
            if (cursor is { } id) query = query.Where(a => a.ApplicationId.CompareTo(id) > 0);
            var rows = await query.OrderBy(a => a.ApplicationId).Take(limit + 1).ToArrayAsync(ct);
            var selected = rows.Take(limit).ToArray();
            var items = selected.Select(row =>
            {
                var snapshot = JsonSerializer.Deserialize<ApplicationEvent>(row.SnapshotJson, PolicyJson.Options)!;
                return new { row.ApplicationId, row.CustomerId, row.Version, row.UpdatedAtUtc, snapshot.Customer.CompanyName,
                    snapshot.Customer.Address.State, snapshot.Application.RequestedAmount, maskedSsn = "***-**-" + snapshot.Customer.Ssn[^4..] };
            });
            return Results.Ok(new { items, totalCount, nextCursor = rows.Length > limit ? Cursor(selected[^1].ApplicationId) : null });
        });
        app.MapGet("/admin/receipts", async (HttpContext http, MockDbContext db, CancellationToken ct) =>
        {
            var (limit, cursor) = Page(http); var query = db.InboxReceipts.AsNoTracking(); var totalCount = await query.CountAsync(ct);
            if (cursor is { } id) query = query.Where(r => r.EventId.CompareTo(id) > 0);
            var rows = await query.OrderBy(r => r.EventId).Take(limit + 1).ToArrayAsync(ct); var selected = rows.Take(limit).ToArray();
            return Results.Ok(new { items = selected.Select(r => new { r.EventId, r.ApplicationId, r.ApplicationVersion, r.Operation, r.ReceivedAtUtc }),
                totalCount, nextCursor = rows.Length > limit ? Cursor(selected[^1].EventId) : null });
        });
    }
    private static string Cursor(Guid id) => Convert.ToBase64String(id.ToByteArray());
    private static (int Limit, Guid? Cursor) Page(HttpContext http)
    {
        var raw = http.Request.Query["limit"].ToString(); var limit = 20;
        if (raw.Length != 0 && (!int.TryParse(raw, out limit) || limit is < 1 or > 100)) throw new PolicyError(400, "INVALID_LIMIT");
        Guid? cursor = null; raw = http.Request.Query["cursor"].ToString();
        if (raw.Length != 0) try { cursor = new Guid(Convert.FromBase64String(raw)); } catch (Exception e) when (e is FormatException or ArgumentException) { throw new PolicyError(400, "INVALID_CURSOR"); }
        return (limit, cursor);
    }
    private static async Task<ApplicationEvent> Body(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) throw new PolicyError(415, "JSON_REQUIRED");
        if (http.Request.ContentLength > 16384) throw new PolicyError(413, "BODY_TOO_LARGE");
        using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
        while ((read = await http.Request.Body.ReadAsync(bytes, ct)) > 0)
        { if (buffer.Length + read > 16384) throw new PolicyError(413, "BODY_TOO_LARGE"); buffer.Write(bytes, 0, read); }
        using var parsed = JsonDocument.Parse(buffer.ToArray());
        try { return JsonSerializer.Deserialize<ApplicationEvent>(buffer.ToArray(), PolicyJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new ValidationFailure(new() { ["body"] = ["Estructura o tipos inválidos."] }); }
    }
}
