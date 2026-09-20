using LoanApp.Core.Domain;
using LoanApp.Core.Rules;

namespace LoanApp.Core.Application;

public sealed record ApprovedApplication(string Decision, Guid CustomerId, Guid ApplicationId,
    long ApplicationVersion, string Operation, Guid PolicyRevisionId, Guid EventId);
public sealed record DeniedApplication(string Decision, Guid PolicyRevisionId, DenialReason[] Reasons);
public sealed record ApplicationResult(ApprovedApplication? Approved, DeniedApplication? Denied);
public sealed record EventCustomer(Guid Id, string FirstName, string LastName, Address Address, string CompanyName, string Ssn);
public sealed record EventApplication(Guid Id, Guid CustomerId, decimal RequestedAmount, string Currency);
public sealed record ApplicationEvent(int SchemaVersion, Guid EventId, string Operation, long ApplicationVersion,
    Guid PolicyRevisionId, EventCustomer Customer, EventApplication Application);
public interface IApplicationStore
{
    Task<ApprovedApplication> SaveApproved(Submission input, Guid revisionId, Guid eventId, Guid correlationId, CancellationToken ct);
    Task RecordDenied(Guid revisionId, Guid[] ruleIds, Guid correlationId, CancellationToken ct);
}
public sealed class ApplicationService(IPolicyStore policies, IApplicationStore store, RuleEngine engine)
{
    public async Task<ApplicationResult> Submit(Submission input, Guid correlationId, CancellationToken ct)
    {
        var normalized = SubmissionValidator.Normalize(input);
        var head = await policies.Head(ct);
        var revision = head.ActiveRevisionId is { } id ? await policies.Revision(id, ct) : null;
        if (revision?.Kind != "Published") throw new PolicyError(503, "POLICY_UNAVAILABLE");
        var evaluation = engine.Evaluate(revision.Id, revision.Document, normalized);
        if (evaluation.Decision == "Denied")
        {
            await store.RecordDenied(revision.Id, evaluation.Reasons.Select(r => r.RuleId).ToArray(), correlationId, ct);
            return new(null, new("Denied", revision.Id, evaluation.Reasons));
        }
        return new(await store.SaveApproved(normalized, revision.Id, Guid.NewGuid(), correlationId, ct), null);
    }
}
