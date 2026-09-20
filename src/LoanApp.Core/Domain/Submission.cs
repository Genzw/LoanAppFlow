using System.Text.RegularExpressions;

namespace LoanApp.Core.Domain;

public sealed record Address(string Line1, string? Line2, string City, string State, string PostalCode);
public sealed record Submission(string FirstName, string LastName, Address Address,
    string CompanyName, decimal RequestedAmount, string Ssn);

public sealed class ValidationFailure(Dictionary<string, string[]> errors) : Exception("Validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public static partial class SubmissionValidator
{
    public static Submission Normalize(Submission input)
    {
        var errors = new Dictionary<string, string[]>();
        string Text(string? value, string field, int max)
        {
            var result = value?.Trim() ?? "";
            if (result.Length is 0 || result.Length > max)
                errors[field] = [$"Debe contener entre 1 y {max} caracteres."];
            return result;
        }
        var first = Text(input.FirstName, "firstName", 100);
        var last = Text(input.LastName, "lastName", 100);
        var company = Text(input.CompanyName, "companyName", 200);
        var line1 = Text(input.Address?.Line1, "address.line1", 200);
        var line2 = input.Address?.Line2?.Trim();
        if (line2?.Length > 200) errors["address.line2"] = ["Máximo 200 caracteres."];
        var city = Text(input.Address?.City, "address.city", 100);
        var postal = Text(input.Address?.PostalCode, "address.postalCode", 20);
        var state = input.Address?.State?.Trim().ToUpperInvariant() ?? "";
        if (!StatePattern().IsMatch(state)) errors["address.state"] = ["Use dos letras ASCII."];
        var ssn = input.Ssn?.Trim() ?? "";
        if (!SsnPattern().IsMatch(ssn)) errors["ssn"] = ["Use nueve dígitos o DDD-DD-DDDD."];
        if (input.RequestedAmount is <= 0 or > 999999999.99m || decimal.Round(input.RequestedAmount, 2) != input.RequestedAmount)
            errors["requestedAmount"] = ["Importe positivo hasta 999999999.99, con máximo dos decimales."];
        if (errors.Count != 0) throw new ValidationFailure(errors);
        return new(first, last, new(line1, string.IsNullOrEmpty(line2) ? null : line2, city, state, postal),
            company, input.RequestedAmount, ssn.Replace("-", ""));
    }

    public static string NormalizeSsn(string value)
    {
        var trimmed = value.Trim();
        if (!SsnPattern().IsMatch(trimmed))
            throw new ValidationFailure(new() { ["ssn"] = ["Formato inválido."] });
        return trimmed.Replace("-", "");
    }

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatePattern();
    [GeneratedRegex("^(?:[0-9]{9}|[0-9]{3}-[0-9]{2}-[0-9]{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex SsnPattern();
}
