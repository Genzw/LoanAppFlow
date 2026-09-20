using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoanApp.Core.Rules;

public sealed record PolicyDocument(int SchemaVersion, RuleDefinition[] Rules, BlacklistEntry[] Blacklist);
public sealed record RuleDefinition(Guid Id, string Code, string Name, string PublicMessage,
    bool Enabled, int Priority, string Effect, string Match, RuleCondition[] Conditions);
public sealed record RuleCondition
{
    // A parameterless JSON constructor keeps absent operands Undefined without exposing
    // an unserializable default JsonElement to the OpenAPI/JSON Schema exporter.
    [JsonConstructor] public RuleCondition() { }
    public RuleCondition(string Field, string Operator, JsonElement Value = default, JsonElement Values = default)
    { this.Field = Field; this.Operator = Operator; this.Value = Value; this.Values = Values; }
    [JsonRequired] public string Field { get; init; } = "";
    [JsonRequired] public string Operator { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Values { get; init; }
}
public sealed record BlacklistEntry(Guid Id, string Ssn);
public sealed record FieldDefinition(string Name, string Type, int? MaxLength, string[] Operators);

public static class RuleCatalog
{
    public static IReadOnlyList<FieldDefinition> Fields { get; } = Array.AsReadOnly(new FieldDefinition[]
    {
        new("firstName", "text", 100, ["equals", "notEquals", "in", "notIn"]),
        new("lastName", "text", 100, ["equals", "notEquals", "in", "notIn"]),
        new("companyName", "text", 200, ["equals", "notEquals", "in", "notIn"]),
        new("address.city", "text", 100, ["equals", "notEquals", "in", "notIn"]),
        new("address.postalCode", "text", 20, ["equals", "notEquals", "in", "notIn"]),
        new("address.state", "state", 2, ["equals", "notEquals", "in", "notIn"]),
        new("requestedAmount", "decimal", null, ["equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual"]),
        new("ssn", "identity", null, ["inBlacklist"])
    });
}

public static class PolicyJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 32
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    public static PolicyDocument Read(string json)
    {
        var policy = JsonSerializer.Deserialize<PolicyDocument>(json, Options) ?? throw new JsonException("Missing policy");
        return PolicyValidator.Normalize(policy);
    }
    public static string Write(PolicyDocument policy) => JsonSerializer.Serialize(policy, Options);
}
