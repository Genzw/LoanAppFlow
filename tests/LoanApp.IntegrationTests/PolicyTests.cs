using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LoanApp.IntegrationTests;

public sealed class PolicyTests
{
    private static AppDbContext Open() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("TEST_APP_DB") ?? throw new InvalidOperationException("Test DB required")).Options);
    private static PolicyService Service(AppDbContext db) => new(new EfPolicyStore(db), new RuleEngine(), TimeProvider.System);
    private static readonly Guid Correlation = Guid.NewGuid();
    private static PolicyDocument Policy => new(1, [new(Guid.NewGuid(), "TEST_RULE", "Test", "Mensaje", true, 10, "Deny", "ALL",
        [new("address.state", "equals", JsonSerializer.SerializeToElement("CA"))])], [new(Guid.NewGuid(), "000000001")]);
    private static Submission Form => new("Ana", "Paz", new("Demo", null, "Demo", "CA", "90001"), "Demo", 100m, "000000003");

    [Fact]
    public async Task Seed_is_idempotent_and_never_overwrites_an_operator_draft()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var service = Service(db);
        var head = await new EfPolicyStore(db).Head(default);
        var draft = await service.CreateDraft(null, PolicyTags.Head(head), Correlation, default);
        Assert.False(await service.Seed(Policy, Correlation, default));
        await service.Discard(PolicyTags.Draft(draft.Revision), Correlation, default);
        Assert.True(await service.Seed(Policy, Correlation, default));
        var first = await new EfPolicyStore(db).Head(default);
        Assert.False(await service.Seed(new(1, [], []), Correlation, default));
        Assert.Equal(first, await new EfPolicyStore(db).Head(default));
        Assert.Single(await db.PolicyRevisions.AsNoTracking().Where(r => r.Kind == "Published").ToArrayAsync());
    }

    [Fact]
    public async Task Publish_is_atomic_snapshots_stay_immutable_and_restore_creates_a_new_revision()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var service = Service(db); var store = new EfPolicyStore(db);
        await service.Seed(Policy, Correlation, default); var originalHead = await store.Head(default);
        var original = await service.Get(originalHead.ActiveRevisionId!.Value, default);
        var created = await service.CreateDraft(original.Id, PolicyTags.Head(originalHead), Correlation, default);
        var edited = await service.ReplaceRules([], PolicyTags.Draft(created.Revision), Correlation, default);
        Assert.Equal(original.Id, (await store.Head(default)).ActiveRevisionId);
        Assert.Equal("Denied", (await service.Simulate(original.Id, Form, null, Correlation, default)).Evaluation.Decision);
        Assert.Equal("Approved", (await service.Simulate(edited.Id, Form, PolicyTags.Draft(edited), Correlation, default)).Evaluation.Decision);
        var validation = await service.Validate(PolicyTags.Draft(edited), default);
        Assert.Contains("APPROVES_ALL_VALID_SUBMISSIONS", validation.Warnings); Assert.True(validation.Summary.HasBaselineDifferences);
        var published = await service.Publish(PolicyTags.Draft(edited), validation.HeadVersion, Correlation, default);
        Assert.Null(published.DraftRevisionId); Assert.Equal(edited.Id, published.ActiveRevisionId);
        Assert.Equal("Approved", (await service.Simulate(published.ActiveRevisionId!.Value, Form, null, Correlation, default)).Evaluation.Decision);
        Assert.Single((await service.Get(original.Id, default)).Document.Rules);
        var error = await Assert.ThrowsAsync<PolicyError>(() => service.ReplaceRules([], PolicyTags.Draft(edited), Correlation, default));
        Assert.Equal(409, error.Status);
        var restored = await service.CreateDraft(original.Id, PolicyTags.Head(published), Correlation, default);
        Assert.NotEqual(original.Id, restored.Revision.Id); Assert.Single(restored.Revision.Document.Rules);
        Assert.Empty(await db.Customers.ToListAsync()); Assert.Empty(await db.Applications.ToListAsync()); Assert.Empty(await db.OutboxMessages.ToListAsync());
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "Policy.Published"));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "Policy.Simulated"));
        Assert.DoesNotContain("000000001", string.Join("", await db.AuditEvents.Select(a => a.MetadataJson).ToArrayAsync()));
    }

    [Fact]
    public async Task Stale_etag_and_recreated_draft_cannot_overwrite_changes()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var service = Service(db); var store = new EfPolicyStore(db);
        var first = await service.CreateDraft(null, PolicyTags.Head(await store.Head(default)), Correlation, default);
        var changed = await service.ReplaceRules(Policy.Rules, PolicyTags.Draft(first.Revision), Correlation, default);
        Assert.Equal(412, (await Assert.ThrowsAsync<PolicyError>(() => service.ReplaceRules([], PolicyTags.Draft(first.Revision), Correlation, default))).Status);
        await service.Discard(PolicyTags.Draft(changed), Correlation, default);
        var second = await service.CreateDraft(null, PolicyTags.Head(await store.Head(default)), Correlation, default);
        Assert.Equal(first.Revision.DraftVersion, second.Revision.DraftVersion);
        Assert.Equal(412, (await Assert.ThrowsAsync<PolicyError>(() => service.ReplaceRules([], PolicyTags.Draft(first.Revision), Correlation, default))).Status);
        Assert.Equal(428, (await Assert.ThrowsAsync<PolicyError>(() => service.ReplaceRules([], null, Correlation, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<PolicyError>(() => service.ReplaceRules([], "invalid", Correlation, default))).Status);
    }

    [Fact]
    public async Task Blacklist_changes_share_draft_etag_and_normalize_identity()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var service = Service(db); var store = new EfPolicyStore(db);
        var created = await service.CreateDraft(null, PolicyTags.Head(await store.Head(default)), Correlation, default);
        var added = await service.AddBlacklist("000-00-0001", PolicyTags.Draft(created.Revision), Correlation, default);
        Assert.Equal("000000001", added.Entry.Ssn);
        Assert.Equal(409, (await Assert.ThrowsAsync<PolicyError>(() => service.AddBlacklist("000000001", PolicyTags.Draft(added.Revision), Correlation, default))).Status);
        var updated = await service.ReplaceRules(Policy.Rules, PolicyTags.Draft(added.Revision), Correlation, default);
        Assert.Single(updated.Document.Blacklist);
        var removed = await service.RemoveBlacklist(added.Entry.Id, PolicyTags.Draft(updated), Correlation, default);
        Assert.Empty(removed.Document.Blacklist);
        var count = await db.AuditEvents.CountAsync();
        await Assert.ThrowsAsync<ValidationFailure>(() => service.ReplaceRules([Policy.Rules[0] with { Conditions = [] }], PolicyTags.Draft(removed), Correlation, default));
        Assert.Equal(count, await db.AuditEvents.CountAsync());
        Assert.Equal(removed.DraftVersion, (await service.Get(removed.Id, default)).DraftVersion);
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_revision_and_head()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var store = new EfPolicyStore(db);
        var head = await store.Head(default); var revision = new PolicySnapshot(Guid.NewGuid(), "Draft", null, 1, DateTimeOffset.UtcNow, null, new(1, [], []));
        var mutation = new PolicyMutation(head, head with { Version = head.Version + 1, DraftRevisionId = revision.Id }, null, revision,
            new(new string('x', 81), revision.Id, Correlation, "Local", 0, 0));
        await Assert.ThrowsAsync<DbUpdateException>(() => store.Commit(mutation, default));
        Assert.Equal(head, await store.Head(default)); Assert.Null(await store.Revision(revision.Id, default));
    }

    [Fact]
    public async Task Competing_initializers_leave_no_orphan_revision_or_audit()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var store = new EfPolicyStore(db);
        var captured = await store.Head(default);
        PolicyMutation Attempt()
        {
            var revision = new PolicySnapshot(Guid.NewGuid(), "Published", null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Policy);
            return new(captured, captured with { Version = captured.Version + 1, ActiveRevisionId = revision.Id, BaselineRevisionId = revision.Id }, null, revision,
                new("Policy.Initialized", revision.Id, Guid.NewGuid(), "Initializer", 1, 1));
        }
        var first = Attempt(); var second = Attempt();
        await store.Commit(first, default); var auditCount = await db.AuditEvents.CountAsync();
        var conflict = await Assert.ThrowsAsync<PolicyConflict>(() => store.Commit(second, default));
        Assert.True(conflict.HeadChanged); Assert.Null(await store.Revision(second.RevisionAfter!.Id, default));
        Assert.Equal(first.RevisionAfter!.Id, (await store.Head(default)).ActiveRevisionId);
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task History_keyset_pagination_does_not_repeat_published_revisions()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(); var store = new EfPolicyStore(db); var service = Service(db);
        await service.Seed(Policy, Correlation, default);
        for (var i = 0; i < 3; i++)
        {
            var head = await store.Head(default);
            var draft = await service.CreateDraft(head.ActiveRevisionId, PolicyTags.Head(head), Correlation, default);
            await service.Publish(PolicyTags.Draft(draft.Revision), draft.Head.Version, Correlation, default);
        }
        var first = await store.History(2, null, null, default);
        var second = await store.History(2, first[^1].PublishedAtUtc, first[^1].Id, default);
        Assert.Equal(4, first.Concat(second).Select(r => r.Id).Distinct().Count());
        Assert.Empty(await store.History(2, second[^1].PublishedAtUtc, second[^1].Id, default));
    }

    [Fact]
    public async Task Http_contracts_mask_ssn_preserve_etags_and_reject_unknown_json()
    {
        await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync();
        await Service(db).Seed(Policy, Correlation, default);
        using var factory = new PolicyApiFactory(db); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:3000");
        var headResponse = await client.GetAsync("/api/admin/policy"); Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        var head = await headResponse.Content.ReadFromJsonAsync<JsonElement>(); var active = head.GetProperty("activeRevisionId").GetGuid();
        var view = await client.GetStringAsync($"/api/admin/policy/revisions/{active}");
        Assert.DoesNotContain("000000001", view); Assert.Contains("***-**-0001", view); Assert.DoesNotContain("documentJson", view);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/policy/draft") { Content = JsonContent.Create(new { sourceRevisionId = active }) };
        create.Headers.TryAddWithoutValidation("If-Match", headResponse.Headers.ETag!.ToString());
        var draft = await client.SendAsync(create); Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var draftTag = draft.Headers.ETag!.ToString();
        using var bad = new HttpRequestMessage(HttpMethod.Put, "/api/admin/policy/draft/rules") { Content = new StringContent("{\"rules\":[],\"script\":\"bad\"}", Encoding.UTF8, "application/json") };
        bad.Headers.TryAddWithoutValidation("If-Match", draftTag);
        var badResponse = await client.SendAsync(bad); Assert.Equal(HttpStatusCode.UnprocessableEntity, badResponse.StatusCode);
        Assert.Equal("application/problem+json", badResponse.Content.Headers.ContentType!.MediaType);
        var problem = await badResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(badResponse.Headers.GetValues("X-Correlation-Id").Single(), problem.GetProperty("traceId").GetString());
        using var noTag = new HttpRequestMessage(HttpMethod.Put, "/api/admin/policy/draft/rules") { Content = JsonContent.Create(new { rules = Array.Empty<object>() }) };
        Assert.Equal((HttpStatusCode)428, (await client.SendAsync(noTag)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/policy/revisions?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/policy/revisions?limit=101")).StatusCode);
        foreach (var (payload, mediaType, expected) in new[]
        {
            ("{", "application/json", 400), ("{\"rules\":null}", "application/json", 422),
            ("not json", "text/plain", 415), (new string(' ', 2097153), "application/json", 413)
        })
        {
            using var invalid = new HttpRequestMessage(HttpMethod.Put, "/api/admin/policy/draft/rules")
            { Content = new StringContent(payload, Encoding.UTF8, mediaType) };
            invalid.Headers.TryAddWithoutValidation("If-Match", draftTag);
            var failure = await client.SendAsync(invalid);
            Assert.Equal(expected, (int)failure.StatusCode);
            Assert.Equal("application/problem+json", failure.Content.Headers.ContentType?.MediaType);
        }
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/policy/draft/validate", null)).StatusCode);
    }

    [Fact]
    public async Task Openapi_describes_real_routes_masked_responses_preconditions_and_limits()
    {
        await using var db = Open(); using var factory = new PolicyApiFactory(db); using var client = factory.CreateClient();
        // Calling the generator directly makes schema failures diagnosable without exposing them over HTTP.
        var provider = factory.Services.GetRequiredKeyedService<Microsoft.AspNetCore.OpenApi.IOpenApiDocumentProvider>("v1");
        await provider.GetOpenApiDocumentAsync(default);
        var response = await client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        Assert.Equal(2, paths.GetProperty("/api/applications").GetProperty("post").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("oneOf").GetArrayLength());
        var create = paths.GetProperty("/api/admin/policy/draft").GetProperty("post");
        Assert.Equal("CreatePolicyDraft", create.GetProperty("operationId").GetString());
        Assert.Contains(create.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "If-Match" && p.GetProperty("required").GetBoolean());
        Assert.True(create.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.True(create.GetProperty("responses").GetProperty("201").GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.True(create.GetProperty("responses").GetProperty("428").GetProperty("content").TryGetProperty("application/problem+json", out _));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.TryGetProperty("PolicyDocument", out _)); Assert.False(schemas.TryGetProperty("BlacklistEntry", out _));
        var masked = schemas.GetProperty("MaskedBlacklistEntry").GetProperty("properties");
        Assert.True(masked.TryGetProperty("maskedSsn", out _)); Assert.False(masked.TryGetProperty("ssn", out _));
        var rules = paths.GetProperty("/api/admin/policy/draft/rules").GetProperty("put");
        Assert.Contains("2097152", rules.GetProperty("requestBody").GetProperty("description").GetString());
        var validate = paths.GetProperty("/api/admin/policy/draft/validate").GetProperty("post");
        Assert.False(validate.TryGetProperty("requestBody", out _));
        var delete = paths.GetProperty("/api/admin/policy/draft").GetProperty("delete").GetProperty("responses").GetProperty("204");
        Assert.False(delete.TryGetProperty("content", out _));
        var condition = schemas.GetProperty("RuleCondition");
        Assert.Equal(RuleCatalog.Fields.Sum(f => f.Operators.Length), condition.GetProperty("anyOf").GetArrayLength());
        Assert.True(schemas.GetProperty("ApiProblemResponse").GetProperty("properties").TryGetProperty("code", out _));
    }

    private sealed class PolicyApiFactory(AppDbContext db) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:AppDb", Environment.GetEnvironmentVariable("TEST_APP_DB"));
            builder.ConfigureServices(services => { services.RemoveAll<AppDbContext>(); services.AddSingleton(db); services.AddTransient<IStartupFilter, LoopbackFilter>(); });
        }
    }
    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, nextMiddleware) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return nextMiddleware(context); }); next(app); };
    }
}
