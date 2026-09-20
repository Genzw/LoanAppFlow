using System.Net;
using System.Text.Json;
using System.Diagnostics;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Mock;
using LoanApp.Mock.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
builder.Services.AddScoped<EventReceiver>();
builder.Services.AddScoped<AuditReader>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["traceId"] = c.HttpContext.Items["CorrelationId"]?.ToString());
builder.Services.AddDbContext<MockDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("MockDb")
    ?? throw new InvalidOperationException("ConnectionStrings__MockDb is required")));
var access = new ServiceAccess(builder.Configuration["App:Profile"], builder.Environment.EnvironmentName, builder.Configuration["Security:IntegrationToken"]);
var app = builder.Build();
app.Use(async (context, next) =>
{
    var correlation = Guid.TryParse(context.Request.Headers["X-Correlation-Id"], out var supplied) ? supplied : Guid.NewGuid();
    context.Items["CorrelationId"] = correlation; context.Response.Headers["X-Correlation-Id"] = correlation.ToString();
    context.Response.Headers.CacheControl = "no-store"; var started = Stopwatch.GetTimestamp();
    try
    {
        access.Authorize(context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip),
            context.Request.Path == "/health/live" || context.Request.Path == "/health/ready", context.Request.Headers["X-Integration-Token"].ToString(),
            context.Request.Method is "POST" or "PUT", null, null, backend: false);
        // Browser-origin mutations are not service-to-service delivery requests.
        if (context.Request.Method is "POST" or "PUT" && context.Request.Headers.ContainsKey("Origin")) throw new PolicyError(403, "SERVICE_ONLY");
        await next();
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        var (status, code) = e switch { PolicyError p => (p.Status, p.Code), ValidationFailure => (422, "VALIDATION_FAILED"),
            JsonException => (400, "INVALID_JSON"), Npgsql.NpgsqlException => (503, "DATABASE_UNAVAILABLE"), _ => (500, "INTERNAL_ERROR") };
        await Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = correlation,
            ["errors"] = (e as ValidationFailure)?.Errors }).ExecuteAsync(context);
    }
    finally { app.Logger.LogInformation("Request {Component} {Action} {Route} {Method} {StatusCode} {DurationMs} {CorrelationId}", "Mock", "HttpRequest",
        (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "unmatched", context.Request.Method,
        context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, correlation); }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (MockDbContext db, CancellationToken ct) =>
{
    try
    {
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) return Results.StatusCode(503);
        await db.InboxReceipts.Take(1).ToListAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.StatusCode(503); }
});
app.MapReceiver();
app.MapGet("/admin/audit", async (HttpContext http, AuditReader reader, CancellationToken ct) =>
    Results.Ok(await reader.Read(AuditQuery.Parse(http.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString()), "Mock"), ct)));
app.Run();
public partial class Program;
