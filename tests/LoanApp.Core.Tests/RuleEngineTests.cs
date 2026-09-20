using System.Globalization;
using System.Text.Json;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using Xunit;

namespace LoanApp.Core.Tests;

public sealed class RuleEngineTests
{
    private static readonly Guid Revision = Guid.NewGuid();
    private static Submission Form => new(" Ana ", " Paz ", new("100 Demo", null, "Demo", " ca ", "90001"), "Demo SA", 10000m, "000-00-0003");
    private static PolicyDocument Baseline => PolicyJson.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "initial-policy.json")));
    private static Evaluation Evaluate(PolicyDocument policy, Submission? form = null) => new RuleEngine().Evaluate(Revision, policy, form ?? Form);

    [Theory]
    [InlineData("NY", "000000003", "STATE_NY")]
    [InlineData(" ny ", "000000003", "STATE_NY")]
    [InlineData("CA", "000-00-0001", "SSN_BLACKLIST")]
    [InlineData("CA", "000000001", "SSN_BLACKLIST")]
    public void Baseline_is_data_driven(string state, string ssn, string expected)
    {
        var result = Evaluate(Baseline, Form with { Address = Form.Address with { State = state }, Ssn = ssn });
        Assert.Equal("Denied", result.Decision);
        Assert.Equal(expected, Assert.Single(result.Reasons).Code);
        Assert.Equal(Revision, result.PolicyRevisionId);
    }

    [Fact]
    public void Reports_all_matches_in_priority_order_without_sensitive_values()
    {
        var result = Evaluate(Baseline, Form with { Address = Form.Address with { State = "NY" }, Ssn = "000000001" });
        Assert.Equal(new[] { "STATE_NY", "SSN_BLACKLIST" }, result.Reasons.Select(r => r.Code));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("\"000000001\"", serialized); Assert.DoesNotContain("100 Demo", serialized);
    }

    [Fact]
    public void No_hidden_baseline_rule_when_policy_is_empty_or_disabled()
    {
        var input = Form with { Address = Form.Address with { State = "NY" }, Ssn = "000000001" };
        Assert.Equal("Approved", Evaluate(new(1, [], []), input).Decision);
        var result = Evaluate(Baseline with { Rules = Baseline.Rules.Select(r => r with { Enabled = false }).ToArray() }, input);
        Assert.Equal("Approved", result.Decision);
        Assert.All(result.Trace, r => { Assert.Null(r.Matched); Assert.Empty(r.Conditions); });
    }

    [Theory]
    [InlineData("equals", 10000, true)]
    [InlineData("notEquals", 10000, false)]
    [InlineData("greaterThan", 10000, false)]
    [InlineData("greaterThanOrEqual", 10000, true)]
    [InlineData("lessThan", 10000, false)]
    [InlineData("lessThanOrEqual", 10000, true)]
    [InlineData("greaterThan", 0, true)]
    [InlineData("greaterThan", 20000, false)]
    public void Numeric_operators_respect_boundary(string op, decimal operand, bool matches)
    {
        var rule = Baseline.Rules[0] with { Conditions = [new("requestedAmount", op, JsonSerializer.SerializeToElement(operand))] };
        Assert.Equal(matches, Assert.Single(Evaluate(new(1, [rule], [])).Trace).Matched);
    }

    [Fact]
    public void Dynamic_rules_change_result_without_special_case_code()
    {
        var rule = Baseline.Rules[0] with { Conditions = [new("companyName", "equals", JsonSerializer.SerializeToElement(" DEMO sa "))] };
        Assert.Equal("Denied", Evaluate(new(1, [rule], [])).Decision);
        Assert.Equal("Approved", Evaluate(new(1, [rule], []), Form with { CompanyName = "Other" }).Decision);
        rule = rule with { Conditions = [new("address.state", "equals", JsonSerializer.SerializeToElement("CA"))] };
        Assert.Equal("Denied", Evaluate(new(1, [rule], [])).Decision);
        Assert.Equal("Approved", Evaluate(new(1, [rule], []), Form with { Address = Form.Address with { State = "NY" } }).Decision);
    }

    [Theory]
    [InlineData("ALL", false)]
    [InlineData("ANY", true)]
    public void Combination_evaluates_all_conditions(string match, bool expected)
    {
        var rule = Baseline.Rules[0] with { Match = match, Conditions = [
            new("firstName", "equals", JsonSerializer.SerializeToElement("ana")),
            new("lastName", "equals", JsonSerializer.SerializeToElement("different"))] };
        var trace = Assert.Single(Evaluate(new(1, [rule], [])).Trace);
        Assert.Equal(expected, trace.Matched); Assert.Equal(2, trace.Conditions.Length);
    }

    [Theory]
    [InlineData("in", true)]
    [InlineData("notIn", false)]
    public void Text_membership_normalizes_operands(string op, bool expected)
    {
        var rule = Baseline.Rules[0] with { Conditions = [new("companyName", op, Values: JsonSerializer.SerializeToElement(new[] { " DEMO sa ", "Else" }))] };
        Assert.Equal(expected, Assert.Single(Evaluate(new(1, [rule], [])).Trace).Matched);
    }

    [Fact]
    public void Comparison_is_culture_independent_and_retains_accents()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var rule = Baseline.Rules[0] with { Conditions = [new("firstName", "equals", JsonSerializer.SerializeToElement("I"))] };
            Assert.Equal("Denied", Evaluate(new(1, [rule], []), Form with { FirstName = "i" }).Decision);
            rule = rule with { Conditions = [new("firstName", "equals", JsonSerializer.SerializeToElement("José"))] };
            Assert.Equal("Approved", Evaluate(new(1, [rule], []), Form with { FirstName = "Jose" }).Decision);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Tie_breaker_uses_ordinal_id_and_does_not_mutate_input()
    {
        var one = Baseline.Rules[0] with { Priority = 1, Conditions = [new("firstName", "equals", JsonSerializer.SerializeToElement("ana"))] };
        var two = one with { Id = Guid.Parse("20000000-0000-4000-8000-000000000001"), Code = "OTHER" };
        var policy = new PolicyDocument(1, [two, one], []); var before = PolicyJson.Write(policy);
        Assert.Equal(new[] { one.Id, two.Id }, Evaluate(policy).Reasons.Select(r => r.RuleId));
        Assert.Equal(before, PolicyJson.Write(policy));
    }

    [Theory]
    [InlineData("00000000")][InlineData("0000000000")][InlineData("000 00 0001")]
    [InlineData("000-00-000X")][InlineData("１２３４５６７８９")]
    public void Invalid_ssn_is_validation_error(string ssn) => Assert.Throws<ValidationFailure>(() => SubmissionValidator.Normalize(Form with { Ssn = ssn }));

    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(0.001)][InlineData(1000000000)]
    public void Invalid_amount_is_validation_error(decimal amount) => Assert.Throws<ValidationFailure>(() => SubmissionValidator.Normalize(Form with { RequestedAmount = amount }));

    [Fact]
    public void Normalization_preserves_fictional_id_and_exact_amount()
    {
        var result = SubmissionValidator.Normalize(Form with { RequestedAmount = 0.01m });
        Assert.Equal("000000003", result.Ssn); Assert.Equal("CA", result.Address.State);
        Assert.Equal("Ana", result.FirstName); Assert.Equal(0.01m, result.RequestedAmount);
        Assert.Equal(999999999.99m, SubmissionValidator.Normalize(Form with { RequestedAmount = 999999999.99m }).RequestedAmount);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"rules\":[],\"blacklist\":[],\"script\":\"alert()\"}")]
    [InlineData("{\"schemaVersion\":1,\"rules\":[]}")]
    [InlineData("{\"schemaVersion\":\"1\",\"rules\":[],\"blacklist\":[]}")]
    public void Unknown_missing_or_coerced_properties_fail(string json) => Assert.Throws<JsonException>(() => PolicyJson.Read(json));

    [Fact]
    public void Invalid_policy_never_defaults_to_approval()
    {
        Assert.Throws<ValidationFailure>(() => Evaluate(new(2, [], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(Baseline with { Rules = [Baseline.Rules[0] with { Conditions = [] }] }));
        Assert.Throws<ValidationFailure>(() => Evaluate(Baseline with { Rules = [Baseline.Rules[0] with { Conditions = [new("ssn", "equals", JsonSerializer.SerializeToElement("000000001"))] }] }));
        Assert.Throws<ValidationFailure>(() => Evaluate(Baseline with { Rules = [Baseline.Rules[0] with { Conditions = [new("requestedAmount", "greaterThan", JsonSerializer.SerializeToElement("10"))] }] }));
    }

    [Fact]
    public void Duplicate_normalized_operands_and_blacklist_fail()
    {
        var rule = Baseline.Rules[0] with { Conditions = [new("address.state", "in", Values: JsonSerializer.SerializeToElement(new[] { "CA", " ca " }))] };
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [], [new(Guid.NewGuid(), "000000001"), new(Guid.NewGuid(), "000-00-0001")])));
    }

    [Fact]
    public void Policy_limits_reject_instead_of_truncating()
    {
        var rule = Baseline.Rules[0];
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, Enumerable.Range(0, 51).Select(i => rule with { Id = Guid.NewGuid(), Code = $"R{i}" }).ToArray(), [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule with { Conditions = Enumerable.Repeat(rule.Conditions[0], 21).ToArray() }], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [], Enumerable.Range(0, 5001).Select(i => new BlacklistEntry(Guid.NewGuid(), i.ToString("D9"))).ToArray())));
        var longOperands = Enumerable.Range(0, 50).Select(i => i.ToString("D2") + new string('a', 198)).ToArray();
        var condition = new RuleCondition("companyName", "in", Values: JsonSerializer.SerializeToElement(longOperands));
        var largeRules = Enumerable.Range(0, 50).Select(i => rule with { Id = Guid.NewGuid(), Code = $"R{i}", Conditions = Enumerable.Repeat(condition, 20).ToArray() }).ToArray();
        Assert.Contains("POLICY_TOO_LARGE", Assert.Throws<ValidationFailure>(() => Evaluate(new(1, largeRules, []))).Errors["policy"]);
    }

    [Fact]
    public void Required_fields_state_and_lengths_produce_field_errors()
    {
        var invalid = Form with { FirstName = " ", LastName = new string('a', 101), CompanyName = "", Address = new("", new string('x', 201), "", "N1", new string('x', 21)) };
        var failure = Assert.Throws<ValidationFailure>(() => SubmissionValidator.Normalize(invalid));
        Assert.Equal(8, failure.Errors.Count);
        Assert.Contains("address.state", failure.Errors.Keys);
    }

    [Fact]
    public void Duplicate_rule_ids_codes_and_invalid_operators_are_rejected_even_when_disabled()
    {
        var rule = Baseline.Rules[0];
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule, rule with { Code = "OTHER" }], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule, rule with { Id = Guid.NewGuid() }], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule with { Enabled = false, Conditions = [new("script", "eval")] }], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule with { Conditions = [new("firstName", "equals", JsonSerializer.SerializeToElement(" "))] }], [])));
        Assert.Throws<ValidationFailure>(() => Evaluate(new(1, [rule with { Conditions = [new("firstName", "equals", JsonSerializer.SerializeToElement(new string('a', 101)))] }], [])));
    }
}
