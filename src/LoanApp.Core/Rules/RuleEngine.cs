using LoanApp.Core.Domain;

namespace LoanApp.Core.Rules;

public sealed record DenialReason(Guid RuleId, string Code, string Message);
public sealed record ConditionTrace(int Index, bool Matched);
public sealed record RuleTrace(Guid RuleId, bool Enabled, bool? Matched, ConditionTrace[] Conditions);
public sealed record Evaluation(string Decision, Guid PolicyRevisionId, DenialReason[] Reasons, RuleTrace[] Trace);

public sealed class RuleEngine
{
    public Evaluation Evaluate(Guid revisionId, PolicyDocument document, Submission input)
    {
        var policy = PolicyValidator.Normalize(document);
        var form = SubmissionValidator.Normalize(input);
        var blacklist = policy.Blacklist.Select(e => e.Ssn).ToHashSet(StringComparer.Ordinal);
        var reasons = new List<DenialReason>(); var trace = new List<RuleTrace>();
        foreach (var rule in policy.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Id.ToString("D"), StringComparer.Ordinal))
        {
            if (!rule.Enabled) { trace.Add(new(rule.Id, false, null, [])); continue; }
            var conditions = rule.Conditions.Select((c, i) => new ConditionTrace(i, Match(c, form, blacklist))).ToArray();
            var matched = rule.Match == "ALL" ? conditions.All(c => c.Matched) : conditions.Any(c => c.Matched);
            trace.Add(new(rule.Id, true, matched, conditions));
            if (matched) reasons.Add(new(rule.Id, rule.Code, rule.PublicMessage));
        }
        return new(reasons.Count == 0 ? "Approved" : "Denied", revisionId, reasons.ToArray(), trace.ToArray());
    }

    private static bool Match(RuleCondition condition, Submission form, HashSet<string> blacklist)
    {
        if (condition.Operator == "inBlacklist") return blacklist.Contains(form.Ssn);
        if (condition.Field == "requestedAmount")
        {
            var operand = condition.Value.GetDecimal();
            return condition.Operator switch
            {
                "equals" => form.RequestedAmount == operand,
                "notEquals" => form.RequestedAmount != operand,
                "greaterThan" => form.RequestedAmount > operand,
                "greaterThanOrEqual" => form.RequestedAmount >= operand,
                "lessThan" => form.RequestedAmount < operand,
                "lessThanOrEqual" => form.RequestedAmount <= operand,
                _ => throw new InvalidOperationException("Invalid validated operator")
            };
        }
        var actual = condition.Field switch
        {
            "firstName" => form.FirstName, "lastName" => form.LastName, "companyName" => form.CompanyName,
            "address.city" => form.Address.City, "address.state" => form.Address.State,
            "address.postalCode" => form.Address.PostalCode,
            _ => throw new InvalidOperationException("Invalid validated field")
        };
        bool Equal(string? text) => string.Equals(actual, text, StringComparison.OrdinalIgnoreCase);
        return condition.Operator switch
        {
            "equals" => Equal(condition.Value.GetString()), "notEquals" => !Equal(condition.Value.GetString()),
            "in" => condition.Values.EnumerateArray().Any(v => Equal(v.GetString())),
            "notIn" => !condition.Values.EnumerateArray().Any(v => Equal(v.GetString())),
            _ => throw new InvalidOperationException("Invalid validated operator")
        };
    }
}
