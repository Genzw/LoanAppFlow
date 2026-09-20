using LoanApp.Api.Infrastructure.Delivery;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;

namespace LoanApp.Api.Endpoints;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnostics(this WebApplication app)
    {
        app.MapGet("/api/admin/applications", async (HttpContext http, DiagnosticsStore store, WorkerWakeup wakeup, CancellationToken ct) =>
        {
            var result = await store.List(Limit(http), Cursor(http), ct); wakeup.Notify(); return Results.Ok(result);
        }).PolicyContract<ApplicationList>("ListApplications", history: true);
        app.MapGet("/api/admin/applications/{id:guid}", async (Guid id, HttpContext http, DiagnosticsStore store, WorkerWakeup wakeup, CancellationToken ct) =>
        {
            long? cursor = null; var raw = http.Request.Query["eventsCursor"].ToString();
            if (raw.Length > 0) { if (!long.TryParse(raw, out var parsed) || parsed <= 0) throw new PolicyError(400, "INVALID_CURSOR"); cursor = parsed; }
            var result = await store.Detail(id, Limit(http), cursor, ct); wakeup.Notify(); return Results.Ok(result);
        }).PolicyContract<ApplicationDetail>("GetApplicationDiagnostic").AddOpenApiOperationTransformer((operation, context, ct) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter { Name = "limit", In = Microsoft.OpenApi.ParameterLocation.Query,
                Description = "Event page size; default 20, range 1..100.", Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Integer, Minimum = "1", Maximum = "100" } });
            operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter { Name = "eventsCursor", In = Microsoft.OpenApi.ParameterLocation.Query,
                Description = "Exclusive upper application version; positive integer returned as events.nextCursor. Events ordered by version descending.", Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Integer, Minimum = "1" } });
            return Task.CompletedTask;
        });
        app.MapGet("/api/admin/external/applications", async (HttpContext http, ExternalReader reader, CancellationToken ct) =>
            Results.Ok(await reader.Read<ExternalApplication>("applications", Limit(http), Cursor(http), (Guid)http.Items["CorrelationId"]!, ct)))
            .PolicyContract<ExternalPage<ExternalApplication>>("ListExternalApplications", history: true);
        app.MapGet("/api/admin/external/receipts", async (HttpContext http, ExternalReader reader, CancellationToken ct) =>
            Results.Ok(await reader.Read<ExternalReceipt>("receipts", Limit(http), Cursor(http), (Guid)http.Items["CorrelationId"]!, ct)))
            .PolicyContract<ExternalPage<ExternalReceipt>>("ListExternalReceipts", history: true);
    }
    private static int Limit(HttpContext http)
    {
        var raw = http.Request.Query["limit"].ToString(); if (raw.Length == 0) return 20;
        if (!int.TryParse(raw, out var limit) || limit is < 1 or > 100) throw new PolicyError(400, "INVALID_LIMIT"); return limit;
    }
    private static Guid? Cursor(HttpContext http)
    {
        var raw = http.Request.Query["cursor"].ToString(); if (raw.Length == 0) return null;
        try { var id = new Guid(Convert.FromBase64String(raw)); if (id == Guid.Empty) throw new FormatException(); return id; }
        catch (Exception e) when (e is FormatException or ArgumentException) { throw new PolicyError(400, "INVALID_CURSOR"); }
    }
}
