using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoanApp.Core.Domain;

namespace LoanApp.Core.Rules;

public static partial class PolicyValidator
{
    public static PolicyDocument Normalize(PolicyDocument document)
    {
        var errors = new Dictionary<string, string[]>();
        void Error(string path, string message) => errors[path] = [message];
        if (document.SchemaVersion != 1) Error("schemaVersion", "Versión no soportada.");
        if (document.Rules is null || document.Rules.Length > 50) Error("rules", "Lista requerida; máximo 50 reglas.");
        if (document.Blacklist is null || document.Blacklist.Length > 5000) Error("blacklist", "Lista requerida; máximo 5000 entradas.");
        if (errors.Count > 0) throw new ValidationFailure(errors);
        var rules = new List<RuleDefinition>();
        var ids = new HashSet<Guid>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rule, index) in document.Rules!.Select((r, i) => (r, i)))
        {
            var path = $"rules[{index}]";
            if (rule is null) { Error(path, "Regla requerida."); continue; }
            if (rule.Id == Guid.Empty || !ids.Add(rule.Id)) Error(path + ".id", "ID vacío o duplicado.");
            if (rule.Code is null || !CodePattern().IsMatch(rule.Code) || !codes.Add(rule.Code)) Error(path + ".code", "Código inválido o duplicado.");
            if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 100) Error(path + ".name", "Nombre de 1 a 100 caracteres.");
            if (string.IsNullOrWhiteSpace(rule.PublicMessage) || rule.PublicMessage.Trim().Length > 200) Error(path + ".publicMessage", "Mensaje de 1 a 200 caracteres.");
            if (rule.Priority is < 0 or > 10000) Error(path + ".priority", "Prioridad fuera de rango.");
            if (rule.Effect != "Deny") Error(path + ".effect", "Solo Deny está soportado.");
            if (rule.Match is not ("ALL" or "ANY")) Error(path + ".match", "Use ALL o ANY.");
            if (rule.Conditions is null || rule.Conditions.Length is < 1 or > 20)
                Error(path + ".conditions", "Entre 1 y 20 condiciones.");
            var conditions = new List<RuleCondition>();
            foreach (var (condition, c) in (rule.Conditions ?? []).Select((v, i) => (v, i)))
            {
                var key = $"{path}.conditions[{c}]";
                var field = RuleCatalog.Fields.FirstOrDefault(f => f.Name == condition?.Field);
                if (condition is null || field is null || !field.Operators.Contains(condition.Operator))
                { Error(key, "Campo u operador no soportado."); continue; }
                var valuePresent = condition.Value.ValueKind != JsonValueKind.Undefined;
                var valuesPresent = condition.Values.ValueKind != JsonValueKind.Undefined;
                if (field.Type == "identity")
                {
                    if (valuePresent || valuesPresent) Error(key, "inBlacklist no admite operandos.");
                    conditions.Add(condition); continue;
                }
                if (field.Type == "decimal")
                {
                    if (valuesPresent || condition.Value.ValueKind != JsonValueKind.Number ||
                        !condition.Value.TryGetDecimal(out var amount) || amount is < 0 or > 999999999.99m || decimal.Round(amount, 2) != amount)
                        Error(key, "Operando decimal inválido.");
                    conditions.Add(condition); continue;
                }
                bool setOperator = condition.Operator is "in" or "notIn";
                if (setOperator ? valuePresent || condition.Values.ValueKind != JsonValueKind.Array : valuesPresent || condition.Value.ValueKind != JsonValueKind.String)
                { Error(key, "Use value o values según el operador."); continue; }
                var elements = setOperator ? condition.Values.EnumerateArray().ToArray() : [condition.Value];
                if (elements.Length is < 1 or > 50) Error(key, "Entre 1 y 50 valores.");
                var normalized = new List<string>();
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in elements)
                {
                    if (element.ValueKind != JsonValueKind.String) { Error(key, "Se requiere texto."); continue; }
                    var text = element.GetString()!.Trim();
                    if (field.Type == "state") text = text.ToUpperInvariant();
                    if (text.Length == 0 || text.Length > field.MaxLength || !unique.Add(text) ||
                        (field.Type == "state" && !StatePattern().IsMatch(text))) Error(key, "Texto inválido o duplicado.");
                    normalized.Add(text);
                }
                conditions.Add(setOperator
                    ? condition with { Values = JsonSerializer.SerializeToElement(normalized) }
                    : condition with { Value = JsonSerializer.SerializeToElement(normalized.FirstOrDefault() ?? "") });
            }
            rules.Add(rule with { Name = rule.Name?.Trim() ?? "", PublicMessage = rule.PublicMessage?.Trim() ?? "", Conditions = conditions.ToArray() });
        }
        var blacklist = new List<BlacklistEntry>();
        var entryIds = new HashSet<Guid>(); var ssns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, index) in document.Blacklist!.Select((v, i) => (v, i)))
        {
            var path = $"blacklist[{index}]";
            if (entry is null || entry.Id == Guid.Empty || !entryIds.Add(entry.Id)) { Error(path, "ID inválido o duplicado."); continue; }
            try
            {
                var ssn = SubmissionValidator.NormalizeSsn(entry.Ssn ?? "");
                if (!ssns.Add(ssn)) Error(path, "Entrada duplicada.");
                blacklist.Add(entry with { Ssn = ssn });
            }
            catch (ValidationFailure) { Error(path, "Identidad inválida."); }
        }
        if (errors.Count > 0) throw new ValidationFailure(errors);
        var result = document with { Rules = rules.ToArray(), Blacklist = blacklist.ToArray() };
        if (Encoding.UTF8.GetByteCount(PolicyJson.Write(result)) > 2 * 1024 * 1024)
            throw new ValidationFailure(new() { ["policy"] = ["POLICY_TOO_LARGE"] });
        return result;
    }
    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,49}$", RegexOptions.CultureInvariant)] private static partial Regex CodePattern();
    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)] private static partial Regex StatePattern();
}
