using LoanApp.Api.Infrastructure.Delivery;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using Microsoft.OpenApi;

namespace LoanApp.Api.Endpoints;
public static class AuditEndpoints
{
    public static void MapAudit(this WebApplication app)
    {
        app.MapGet("/api/admin/audit", async (HttpContext http, AuditReader reader, CancellationToken ct) =>
            Results.Ok(await reader.Read(AuditQuery.Parse(Values(http), "Api"), ct))).AuditContract("ReadApiAudit");
        app.MapGet("/api/admin/external/audit", async (HttpContext http, ExternalReader reader, CancellationToken ct) =>
        {
            var values = Values(http); var query = AuditQuery.Parse(values, "Mock");
            return Results.Ok(await reader.ReadAudit(values, query.Limit, (Guid)http.Items["CorrelationId"]!, ct));
        }).AuditContract("ReadMockAudit");
    }
    private static Dictionary<string, string> Values(HttpContext http) => http.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString());
    private static void AuditContract(this RouteHandlerBuilder route, string name)
    {
        route.PolicyContract<AuditPage>(name, history: true).AddOpenApiOperationTransformer((operation, context, ct) =>
        {
            operation.Parameters ??= [];
            foreach (var key in new[] { "fromUtc", "toUtc", "action", "entityType", "entityId", "correlationId" })
                operation.Parameters.Add(new OpenApiParameter { Name = key, In = ParameterLocation.Query,
                    Description = key == "fromUtc" ? "Inclusive UTC ISO-8601 timestamp." : key == "toUtc" ? "Exclusive UTC ISO-8601 timestamp." : "Exact match; changing filters invalidates the cursor.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
            return Task.CompletedTask;
        });
    }
}
