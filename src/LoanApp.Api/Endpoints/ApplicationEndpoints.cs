using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Api.Infrastructure.Delivery;

namespace LoanApp.Api.Endpoints;

public static class ApplicationEndpoints
{
    public static void MapApplications(this WebApplication app)
    {
        app.MapPost("/api/applications", async (HttpContext http, ApplicationService service, WorkerWakeup wakeup, CancellationToken ct) =>
        {
            var input = await PolicyEndpoints.Body<Submission>(http, 16384, ct);
            var result = await service.Submit(input, (Guid)http.Items["CorrelationId"]!, ct);
            if (result.Approved is not null) wakeup.Notify();
            return result.Approved is { } approved ? Results.Ok((object)approved) : Results.Ok((object)result.Denied!);
        }).Accepts<Submission>("application/json").PolicyContract<ApprovedApplication>("SubmitApplication", bodyLimit: 16384)
            .WithTags("Applications").AddOpenApiOperationTransformer(async (operation, context, ct) =>
            {
                var approved = await context.GetOrCreateSchemaAsync(typeof(ApprovedApplication), cancellationToken: ct);
                var denied = await context.GetOrCreateSchemaAsync(typeof(DeniedApplication), cancellationToken: ct);
                operation.Responses!["200"].Content!["application/json"].Schema = new Microsoft.OpenApi.OpenApiSchema { OneOf = [approved, denied] };
            });
    }
}
