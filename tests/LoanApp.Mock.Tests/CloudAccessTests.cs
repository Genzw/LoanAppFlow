using System.Net;
using System.Security.Cryptography;
using LoanApp.Mock.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
namespace LoanApp.Mock.Tests;
public sealed class CloudAccessTests : IsolatedMockDatabase
{
    [Fact]
    public async Task Mock_cloud_requires_its_own_token_for_reads_and_writes()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = new Factory(this, token); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/applications", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Backend-Token", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/audit")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Integration-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/receipts")).StatusCode);
        await using var db = Open(); Assert.Empty(await db.AuditEvents.ToArrayAsync()); Assert.Empty(await db.Applications.ToArrayAsync());
    }
    private sealed class Factory(CloudAccessTests owner, string token) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); builder.UseSetting("App:Profile", "CloudDemo"); builder.UseSetting("Security:IntegrationToken", token);
            builder.UseSetting("ConnectionStrings:MockDb", Environment.GetEnvironmentVariable("TEST_MOCK_DB"));
            builder.ConfigureServices(services => { services.RemoveAll<DbContextOptions<MockDbContext>>(); services.AddScoped(_ => owner.Options()); });
        }
    }
}
