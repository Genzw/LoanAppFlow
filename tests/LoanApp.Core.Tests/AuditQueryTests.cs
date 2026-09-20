using LoanApp.Core.Application;
using Xunit;

namespace LoanApp.Core.Tests;
public sealed class AuditQueryTests
{
    [Theory]
    [InlineData("limit", "101")] [InlineData("limit", "0")] [InlineData("entityId", "no-guid")]
    [InlineData("correlationId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("fromUtc", "2026-09-18T00:00:00")]
    [InlineData("toUtc", "2026-09-18T00:00:00+01:00")]
    [InlineData("action", "' OR 1=1")]
    [InlineData("cursor", "invalid")]
    public void Invalid_filters_fail_explicitly(string key, string value) => Assert.Equal(400,
        Assert.Throws<PolicyError>(() => AuditQuery.Parse(new Dictionary<string, string> { [key] = value }, "Api")).Status);
    [Fact]
    public void Metadata_projects_only_safe_typed_fields()
    {
        var safe = AuditRow.SafeMetadata("""{"applicationVersion":"000000003","changedFields":["ssn"],"affectedRuleIds":["secret"],"operation":"private","attemptCount":2,"token":"secret"}""");
        Assert.Single(safe); Assert.Equal(2, safe["attemptCount"].GetInt32()); Assert.Empty(AuditRow.SafeMetadata("[]"));
    }
}
