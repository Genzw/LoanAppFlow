using System.Text;
using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;

namespace LoanApp.Api.Endpoints;

public static class PolicyEndpoints
{
    public static void MapPolicies(this WebApplication app)
    {
        app.MapGet("/api/admin/policy", async (IPolicyStore store, HttpContext http, CancellationToken ct) =>
        {
            var head = await store.Head(ct); http.Response.Headers.ETag = PolicyTags.Head(head);
            return Results.Ok(HeadView(head));
        }).PolicyContract<HeadResponse>("GetPolicy", etag: "Head");
        app.MapGet("/api/admin/policy/revisions", async (IPolicyStore store, HttpContext http, CancellationToken ct) =>
        {
            var limitText = http.Request.Query["limit"].ToString();
            int limit = 20;
            if (limitText.Length > 0 && (!int.TryParse(limitText, out limit) || limit is < 1 or > 100)) throw new PolicyError(400, "INVALID_LIMIT");
            HistoryCursor? cursor = null;
            var encoded = http.Request.Query["cursor"].ToString();
            if (encoded.Length > 0)
            {
                try { cursor = JsonSerializer.Deserialize<HistoryCursor>(Convert.FromBase64String(encoded), PolicyJson.Options) ?? throw new JsonException(); }
                catch (Exception e) when (e is FormatException or JsonException) { throw new PolicyError(400, "INVALID_CURSOR"); }
                if (cursor.Id == Guid.Empty || cursor.Date.Offset != TimeSpan.Zero) throw new PolicyError(400, "INVALID_CURSOR");
            }
            var head = await store.Head(ct);
            var rows = await store.History(limit + 1, cursor?.Date, cursor?.Id, ct); var items = rows.Take(limit).ToArray();
            string? next = rows.Count > limit ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new HistoryCursor(items[^1].PublishedAtUtc!.Value, items[^1].Id), PolicyJson.Options)) : null;
            return Results.Ok(new HistoryResponse(items.Select(r => new HistoryItem(r.Id, r.BaseRevisionId, r.PublishedAtUtc, r.Id == head.ActiveRevisionId)).ToArray(), next));
        }).PolicyContract<HistoryResponse>("ListPolicyRevisions", history: true);
        app.MapGet("/api/admin/policy/revisions/{id:guid}", async (Guid id, PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var revision = await service.Get(id, ct);
            if (revision.Kind == "Draft") http.Response.Headers.ETag = PolicyTags.Draft(revision);
            return Results.Ok(View(revision));
        }).PolicyContract<RevisionResponse>("GetPolicyRevision", etag: "Draft only");
        app.MapPost("/api/admin/policy/draft", async (PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var body = await Body<CreateDraftInput>(http, 16384, ct);
            var result = await service.CreateDraft(body.SourceRevisionId, Tag(http), Correlation(http), ct);
            http.Response.Headers.ETag = PolicyTags.Draft(result.Revision);
            return Results.Json(new DraftResponse(View(result.Revision), result.Head.Version), statusCode: 201);
        }).Accepts<CreateDraftInput>("application/json").PolicyContract<DraftResponse>("CreatePolicyDraft", 201, "Head", "Draft", bodyLimit: 16384);
        app.MapPut("/api/admin/policy/draft/rules", async (PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var body = await Body<RulesInput>(http, 2097152, ct);
            var revision = await service.ReplaceRules(body.Rules, Tag(http), Correlation(http), ct);
            http.Response.Headers.ETag = PolicyTags.Draft(revision); return Results.Ok(View(revision));
        }).Accepts<RulesInput>("application/json").PolicyContract<RevisionResponse>("ReplacePolicyRules", ifMatch: "Draft", etag: "Draft", bodyLimit: 2097152);
        app.MapPost("/api/admin/policy/draft/blacklist", async (PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var body = await Body<BlacklistInput>(http, 16384, ct);
            var result = await service.AddBlacklist(body.Ssn, Tag(http), Correlation(http), ct);
            http.Response.Headers.ETag = PolicyTags.Draft(result.Revision);
            return Results.Json(new MaskedBlacklistEntry(result.Entry.Id, Mask(result.Entry.Ssn)), statusCode: 201);
        }).Accepts<BlacklistInput>("application/json").PolicyContract<MaskedBlacklistEntry>("AddBlacklistEntry", 201, "Draft", "Draft", bodyLimit: 16384);
        app.MapDelete("/api/admin/policy/draft/blacklist/{id:guid}", async (Guid id, PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var revision = await service.RemoveBlacklist(id, Tag(http), Correlation(http), ct);
            http.Response.Headers.ETag = PolicyTags.Draft(revision); return Results.NoContent();
        }).PolicyContract<object>("RemoveBlacklistEntry", 204, "Draft", "Draft");
        app.MapPost("/api/admin/policy/draft/validate", async (PolicyService service, HttpContext http, CancellationToken ct) =>
            Results.Ok(await service.Validate(Tag(http), ct))).PolicyContract<PolicyValidation>("ValidatePolicyDraft", ifMatch: "Draft");
        app.MapPost("/api/admin/policy/draft/publish", async (PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var body = await Body<PublishInput>(http, 16384, ct);
            if (body.ExpectedHeadVersion is not { } version || version < 1) throw new ValidationFailure(new() { ["expectedHeadVersion"] = ["Versión requerida."] });
            return Results.Ok(HeadView(await service.Publish(Tag(http), version, Correlation(http), ct)));
        }).Accepts<PublishInput>("application/json").PolicyContract<HeadResponse>("PublishPolicyDraft", ifMatch: "Draft", bodyLimit: 16384);
        app.MapDelete("/api/admin/policy/draft", async (PolicyService service, HttpContext http, CancellationToken ct) =>
        { await service.Discard(Tag(http), Correlation(http), ct); return Results.NoContent(); }).PolicyContract<object>("DiscardPolicyDraft", 204, "Draft");
        app.MapPost("/api/admin/policy/revisions/{id:guid}/simulate", async (Guid id, PolicyService service, HttpContext http, CancellationToken ct) =>
        {
            var body = await Body<Submission>(http, 16384, ct);
            var result = await service.Simulate(id, body, Tag(http), Correlation(http), ct);
            return Results.Ok(new SimulationResponse(result.Evaluation.Decision, result.Evaluation.PolicyRevisionId, result.DraftVersion, result.Evaluation.Reasons, result.Evaluation.Trace));
        }).Accepts<Submission>("application/json").PolicyContract<SimulationResponse>("SimulatePolicyRevision", ifMatch: "Draft only", bodyLimit: 16384);
    }
    private static string? Tag(HttpContext http) => http.Request.Headers.IfMatch.FirstOrDefault();
    private static Guid Correlation(HttpContext http) => (Guid)http.Items["CorrelationId"]!;
    private static HeadResponse HeadView(PolicyPointers h) => new(h.Version, h.ActiveRevisionId, h.DraftRevisionId, h.BaselineRevisionId);
    public static RevisionResponse View(PolicySnapshot r) => new(r.Id, r.Kind, r.BaseRevisionId, r.DraftVersion,
        r.CreatedAtUtc, r.PublishedAtUtc, r.Document.SchemaVersion, r.Document.Rules,
        r.Document.Blacklist.Select(e => new MaskedBlacklistEntry(e.Id, Mask(e.Ssn))).ToArray());
    private static string Mask(string ssn) => "***-**-" + ssn[^4..];
    internal static async Task<T> Body<T>(HttpContext http, int maxBytes, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) throw new PolicyError(415, "JSON_REQUIRED");
        if (http.Request.ContentLength > maxBytes) throw new PolicyError(413, "BODY_TOO_LARGE");
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > maxBytes) throw new PolicyError(413, "BODY_TOO_LARGE");
            buffer.Write(chunk, 0, count);
        }
        var bytes = buffer.ToArray();
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try { return JsonSerializer.Deserialize<T>(bytes, PolicyJson.Options) ?? throw new JsonException("JSON required"); }
        catch (JsonException) { throw new ValidationFailure(new() { ["body"] = ["Estructura o tipos no válidos; no se admiten propiedades desconocidas."] }); }
    }
    private sealed record HistoryCursor(DateTimeOffset Date, Guid Id);
}
