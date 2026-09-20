using Microsoft.OpenApi;
using LoanApp.Core.Rules;
using System.Text.Json.Nodes;

namespace LoanApp.Api.Endpoints;

public static class PolicyOpenApi
{
    public static Task DescribeSchema(OpenApiSchema schema, Microsoft.AspNetCore.OpenApi.OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (context.JsonTypeInfo.Type != typeof(RuleCondition)) return Task.CompletedTask;
        schema.Description = "One typed condition from the technical catalog. No scripts, nesting or SSN literals. Text uses trim and ordinal case-insensitive comparison.";
        schema.AnyOf = [];
        foreach (var field in RuleCatalog.Fields)
        foreach (var op in field.Operators)
        {
            var variant = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false,
                Required = new HashSet<string> { "field", "operator" }, Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["field"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = new List<JsonNode> { JsonValue.Create(field.Name)! } },
                    ["operator"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = new List<JsonNode> { JsonValue.Create(op)! } }
                } };
            if (op != "inBlacklist")
            {
                var operand = field.Type == "decimal"
                    ? new OpenApiSchema { Type = JsonSchemaType.Number, Minimum = "0", Maximum = "999999999.99", MultipleOf = 0.01m }
                    : new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = field.MaxLength,
                        Description = field.Type == "state" ? "Trimmed input must be two ASCII letters; normalized to uppercase." : "Trimmed value must not be empty." };
                var list = op is "in" or "notIn";
                var key = list ? "values" : "value";
                variant.Required.Add(key);
                variant.Properties[key] = list ? new OpenApiSchema { Type = JsonSchemaType.Array, MinItems = 1, MaxItems = 50,
                    UniqueItems = true, Items = operand, Description = "Duplicates after normalization are rejected." } : operand;
            }
            schema.AnyOf.Add(variant);
        }
        return Task.CompletedTask;
    }

    public static RouteHandlerBuilder PolicyContract<T>(this RouteHandlerBuilder route, string name,
        int status = 200, string? ifMatch = null, string? etag = null, bool history = false, int? bodyLimit = null)
    {
        route.WithName(name).WithTags("Policies");
        if (status == 204) route.Produces(204); else route.Produces<T>(status);
        foreach (var code in new[] { 400, 401, 403, 404, 409, 412, 413, 415, 422, 428, 500, 503 }) route.Produces<ApiProblemResponse>(code, "application/problem+json");
        return route.AddOpenApiOperationTransformer((operation, context, ct) =>
        {
            operation.Parameters ??= [];
            if (ifMatch is not null) operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match", In = ParameterLocation.Header, Required = ifMatch != "Draft only",
                Description = ifMatch == "Head" ? "Strong head ETag returned by GetPolicy. Missing: 428; stale: 412."
                    : "Strong revision ETag; required for Draft, omitted for Published simulations. Missing: 428; stale: 412.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
            if (context.Description.HttpMethod != "GET") operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Origin", In = ParameterLocation.Header, Required = true,
                Description = "Exact configured LocalWebOrigin; default http://127.0.0.1:3000.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }
            });
            if (history)
            {
                operation.Parameters.Add(new OpenApiParameter { Name = "limit", In = ParameterLocation.Query,
                    Description = "Page size, default 20; range 1..100.", Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100" } });
                operation.Parameters.Add(new OpenApiParameter { Name = "cursor", In = ParameterLocation.Query,
                    Description = "Opaque nextCursor from the preceding page.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
            }
            if (operation.RequestBody is { } body && bodyLimit is { } limit)
                body.Description = $"JSON only, maximum {limit} UTF-8 bytes. Unknown properties and invalid types: 422; malformed JSON: 400.";
            foreach (var (code, value) in operation.Responses!)
            {
                if (value is not OpenApiResponse response) continue;
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["Cache-Control"] = new OpenApiHeader { Description = "no-store", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                response.Headers["X-Correlation-Id"] = new OpenApiHeader { Description = "Request correlation identifier.", Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" } };
                if (etag is not null && code == status.ToString()) response.Headers["ETag"] = new OpenApiHeader
                { Description = etag + " strong ETag.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                if (code is "400" or "403" or "404" or "409" or "412" or "413" or "415" or "422" or "428" or "500" or "503")
                    response.Description += " Problem Details includes code, traceId and optional errors keyed by field. No request data or stack traces.";
            }
            return Task.CompletedTask;
        });
    }
}
