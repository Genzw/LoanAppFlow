using System.Net;
using System.Text.Json;
using System.Diagnostics;
using LoanApp.Api.Endpoints;
using LoanApp.Api.Infrastructure.Delivery;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => { options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    if (context.HttpContext.Items["CorrelationId"] is Guid id) context.ProblemDetails.Extensions["traceId"] = id.ToString();
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer(PolicyOpenApi.DescribeSchema);
    options.AddDocumentTransformer((document, context, ct) =>
{
    document.Info.Title = "LoanAppFlow local policy API";
    document.Info.Version = "v1";
    document.Info.Description = "Generated from endpoints. Normative requirements: SDD.md. LocalDevelopment or authenticated CloudDemo; cloud deployment verification remains pending.";
    return Task.CompletedTask;
    });
});
var access = new ServiceAccess(builder.Configuration["App:Profile"], builder.Environment.EnvironmentName,
    builder.Configuration["Security:BackendToken"], builder.Configuration["Security:DemoOrigin"], backend: true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<DemoAuditInterceptor>();
builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPolicyStore, EfPolicyStore>();
builder.Services.AddScoped<PolicyService>();
builder.Services.AddScoped<IApplicationStore, EfApplicationStore>();
builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<OutboxStore>();
builder.Services.AddSingleton<WorkerWakeup>();
builder.Services.AddSingleton(new WorkerCadence(access.Cloud));
builder.Services.AddScoped<DiagnosticsStore>();
builder.Services.AddScoped<AuditReader>();
var externalDestination = access.Cloud ? ServiceAccess.RequireOrigin(builder.Configuration["ExternalService:BaseUrl"])
    : new Uri(builder.Configuration["Outbox:BaseUrl"] ?? "http://127.0.0.1:5200/");
var externalToken = access.Cloud ? ServiceAccess.RequireToken(builder.Configuration["ExternalService:Token"]) : null;
if (access.Cloud && externalToken == builder.Configuration["Security:BackendToken"]) throw new InvalidOperationException("Service tokens must differ.");
if (!access.Cloud && (!externalDestination.IsLoopback || externalDestination.Scheme != "http" || externalDestination.AbsolutePath != "/" || externalDestination.UserInfo.Length != 0 || externalDestination.Query.Length != 0 || externalDestination.Fragment.Length != 0))
    throw new InvalidOperationException("Local external service requires a loopback HTTP origin.");
builder.Services.AddHttpClient<ExternalReader>(http => { http.BaseAddress = externalDestination; http.Timeout = TimeSpan.FromSeconds(10); if (externalToken is not null) http.DefaultRequestHeaders.Add("X-Integration-Token", externalToken); })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
if (builder.Configuration.GetValue<bool>("Outbox:Enabled"))
{
    var destination = externalDestination;
    builder.Services.AddHttpClient<DeliveryClient>(http => { http.BaseAddress = destination; http.Timeout = TimeSpan.FromSeconds(10); if (externalToken is not null) http.DefaultRequestHeaders.Add("X-Integration-Token", externalToken); })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHostedService<OutboxWorker>();
}
builder.Services.AddDbContext<AppDbContext>((services, options) => options.AddInterceptors(services.GetRequiredService<DemoAuditInterceptor>()).UseNpgsql(
    builder.Configuration.GetConnectionString("AppDb") ?? throw new InvalidOperationException("ConnectionStrings__AppDb is required")));


var app = builder.Build();
if (Array.IndexOf(args, "--seed-policy") is var seedIndex && seedIndex >= 0)
{
    if (seedIndex + 1 >= args.Length) throw new InvalidOperationException("Policy file required");
    using var scope = app.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<PolicyService>();
    var document = PolicyJson.Read(await File.ReadAllTextAsync(args[seedIndex + 1]));
    var created = await service.Seed(document, Guid.NewGuid(), CancellationToken.None);
    Console.WriteLine(created ? "Policy initialized." : "Initialization skipped: policy state already exists.");
    return;
}
app.Use(async (context, next) =>
{
    var correlation = Guid.NewGuid(); context.Items["CorrelationId"] = correlation;
    var started = Stopwatch.GetTimestamp();
    context.Response.Headers["X-Correlation-Id"] = correlation.ToString();
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        var session = access.Authorize(context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip),
            context.Request.Path == "/health/live" || context.Request.Path == "/health/ready", context.Request.Headers["X-Backend-Token"].ToString(),
            context.Request.Method is "POST" or "PUT" or "DELETE", context.Request.Headers.Origin.ToString(), context.Request.Headers["X-Demo-Session"].ToString(), backend: true);
        if (session is { } actor) context.Items["DemoSessionId"] = actor;
        if (!access.Cloud && context.Request.Method is "POST" or "PUT" or "DELETE")
        {
            var origin = context.Request.Headers.Origin.ToString();
            var allowedOrigin = builder.Configuration["LocalWebOrigin"] ?? "http://127.0.0.1:3000";
            if (origin != allowedOrigin) throw new PolicyError(403, "ORIGIN_NOT_ALLOWED");
        }
        await next();
        // Routing can reject content types (Accepts metadata) before the handler runs.
        // Preserve the same safe Problem Details contract for those empty responses.
        if (context.Response.StatusCode >= 400 && !context.Response.HasStarted &&
            context.Response.ContentType is null && context.Response.ContentLength is null or 0)
            throw new PolicyError(context.Response.StatusCode, context.Response.StatusCode switch
            {
                400 => "INVALID_REQUEST", 404 => "NOT_FOUND", 405 => "METHOD_NOT_ALLOWED",
                413 => "BODY_TOO_LARGE", 415 => "JSON_REQUIRED", _ => "REQUEST_FAILED"
            });
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        var (status, code) = error switch
        {
            PolicyError p => (p.Status, p.Code), ValidationFailure => (422, "VALIDATION_FAILED"),
            JsonException => (400, "INVALID_JSON"), BadHttpRequestException b => (b.StatusCode, "INVALID_REQUEST"),
            NpgsqlException => (503, "DATABASE_UNAVAILABLE"), _ => (500, "INTERNAL_ERROR")
        };
        app.Logger.LogWarning("Request failed: {Code}; correlation {CorrelationId}", code, correlation);
        await Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?>
        {
            ["code"] = code, ["traceId"] = correlation, ["errors"] = (error as ValidationFailure)?.Errors
        }).ExecuteAsync(context);
    }
    finally
    {
        app.Logger.LogInformation("Request completed {Component} {Action} {Route} {Method} {StatusCode} {DurationMs} {CorrelationId}",
            "Api", "HttpRequest", (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
            context.Request.Method, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, correlation);
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            return Results.Json(new { status = "notReady", code = "SCHEMA_PENDING" }, statusCode: 503);
        var revision = await (from head in db.PolicyHeads where head.Id == 1
            join policy in db.PolicyRevisions on head.ActiveRevisionId equals policy.Id
            where policy.Kind == "Published" select policy.DocumentJson).SingleOrDefaultAsync(ct);
        if (revision is null) return Results.Json(new { status = "notReady", code = "POLICY_UNAVAILABLE" }, statusCode: 503);
        PolicyJson.Read(revision);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { return Results.Json(new { status = "notReady", code = "DEPENDENCY_UNAVAILABLE" }, statusCode: 503); }
});
app.MapGet("/api/admin/rule-catalog", () => Results.Ok(new CatalogResponse(1, RuleCatalog.Fields,
    new(50, 20, 5000, 2097152)))).PolicyContract<CatalogResponse>("GetRuleCatalog");
app.MapPolicies();
app.MapApplications();
app.MapDiagnostics();
app.MapAudit();
app.MapPost("/api/admin/outbox/{eventId:guid}/retry", async (Guid eventId, HttpContext http, OutboxStore store, WorkerWakeup wakeup, CancellationToken ct) =>
{
    await store.Retry(eventId, (Guid)http.Items["CorrelationId"]!, ct);
    wakeup.Notify();
    return Results.Ok(new { eventId, status = "Pending" });
}).PolicyContract<object>("RetryOutboxEvent");
app.MapOpenApi();
app.Run();
public partial class Program;
