using LoanApp.Core.Domain;

namespace LoanApp.Core.Application;

public sealed record DeliveryReceipt(Guid EventId, Guid ApplicationId, long ApplicationVersion, bool Duplicate);
public static class EventContract
{
    public static ApplicationEvent Normalize(ApplicationEvent input)
    {
        if (input.SchemaVersion != 1 || input.EventId == Guid.Empty || input.PolicyRevisionId == Guid.Empty ||
            input.Customer is null || input.Application is null || input.Customer.Id == Guid.Empty || input.Application.Id == Guid.Empty ||
            input.Customer.Id != input.Application.CustomerId || input.Application.Currency != "USD" ||
            !(input.Operation == "Created" && input.ApplicationVersion == 1 || input.Operation == "Updated" && input.ApplicationVersion >= 2))
            throw new ValidationFailure(new() { ["event"] = ["Identidad, versión u operación no válida."] });
        var form = SubmissionValidator.Normalize(new(input.Customer.FirstName, input.Customer.LastName, input.Customer.Address,
            input.Customer.CompanyName, input.Application.RequestedAmount, input.Customer.Ssn));
        var amount = decimal.Parse(form.RequestedAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        return input with { Customer = input.Customer with { FirstName = form.FirstName, LastName = form.LastName,
            Address = form.Address, CompanyName = form.CompanyName, Ssn = form.Ssn }, Application = input.Application with { RequestedAmount = amount } };
    }
}
