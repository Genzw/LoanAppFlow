using System.Security.Cryptography;
using LoanApp.Core.Application;
using Xunit;
namespace LoanApp.Core.Tests;
public sealed class ServiceAccessTests
{
    [Fact]
    public void Cloud_requires_token_session_and_exact_mutation_origin_even_on_loopback()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var actor = Guid.NewGuid();
        var access = new ServiceAccess("CloudDemo", "Production", token, "https://demo.example", true);
        Assert.Equal(401, Assert.Throws<PolicyError>(() => access.Authorize(true, false, null, false, null, null, true)).Status);
        Assert.Equal(401, Assert.Throws<PolicyError>(() => access.Authorize(true, false, token, false, null, null, true)).Status);
        Assert.Equal(403, Assert.Throws<PolicyError>(() => access.Authorize(false, false, token, true, "https://evil.example", actor.ToString(), true)).Status);
        Assert.Equal(actor, access.Authorize(false, false, token, true, "https://demo.example", actor.ToString(), true));
        Assert.Null(access.Authorize(false, true, null, false, null, null, true));
    }
    [Fact]
    public void Local_is_development_loopback_only_and_configuration_has_no_cloud_fallback()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceAccess(null, "Production", null));
        Assert.Throws<InvalidOperationException>(() => new ServiceAccess("CloudDemo", "Production", "short"));
        Assert.Throws<InvalidOperationException>(() => ServiceAccess.RequireOrigin("http://demo.example"));
        Assert.Throws<InvalidOperationException>(() => ServiceAccess.RequireOrigin("https://demo.example/path"));
        var local = new ServiceAccess(null, "Development", null);
        Assert.Equal(403, Assert.Throws<PolicyError>(() => local.Authorize(false, false, null, false, null, null, false)).Status);
        Assert.Null(local.Authorize(true, false, null, false, null, null, false));
    }
}
