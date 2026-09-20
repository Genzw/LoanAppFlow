using System.Text.Json;
using LoanApp.Core.Application;
using LoanApp.Core.Domain;
using LoanApp.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LoanApp.Api.Infrastructure.Persistence;

public sealed class EfApplicationStore(DbContextOptions<AppDbContext> options, TimeProvider clock) : IApplicationStore
{
    public async Task<ApprovedApplication> SaveApproved(Submission input, Guid revisionId, Guid eventId, Guid correlationId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = new AppDbContext(options);
            // A single statement provides a coherent customer/application pair.
            var aggregate = await (from customer in db.Customers where customer.Ssn == input.Ssn
                join application in db.Applications on customer.Id equals application.CustomerId into loans
                from application in loans.DefaultIfEmpty() select new { Customer = customer, Application = application }).SingleOrDefaultAsync(ct);
            if (aggregate is not null && aggregate.Application is null) throw new PolicyError(500, "AGGREGATE_INTEGRITY_ERROR");
            var now = clock.GetUtcNow(); var created = aggregate is null;
            var customerRow = aggregate?.Customer ?? new Customer { Id = Guid.NewGuid(), Ssn = input.Ssn,
                FirstName = input.FirstName, LastName = input.LastName, AddressLine1 = input.Address.Line1, City = input.Address.City,
                State = input.Address.State, PostalCode = input.Address.PostalCode, CompanyName = input.CompanyName, CreatedAtUtc = now };
            var applicationRow = aggregate?.Application ?? new LoanApplication { Id = Guid.NewGuid(), CustomerId = customerRow.Id, CreatedAtUtc = now };
            var changed = ChangedFields(customerRow, applicationRow, input, created);
            customerRow.FirstName = input.FirstName; customerRow.LastName = input.LastName; customerRow.CompanyName = input.CompanyName;
            customerRow.AddressLine1 = input.Address.Line1; customerRow.AddressLine2 = input.Address.Line2; customerRow.City = input.Address.City;
            customerRow.State = input.Address.State; customerRow.PostalCode = input.Address.PostalCode; customerRow.UpdatedAtUtc = now;
            applicationRow.RequestedAmount = input.RequestedAmount; applicationRow.Version++; applicationRow.LastPolicyRevisionId = revisionId; applicationRow.UpdatedAtUtc = now;
            if (created) { db.Customers.Add(customerRow); db.Applications.Add(applicationRow); }
            var operation = created ? "Created" : "Updated";
            var payload = new ApplicationEvent(1, eventId, operation, applicationRow.Version, revisionId,
                new(customerRow.Id, input.FirstName, input.LastName, input.Address, input.CompanyName, input.Ssn),
                new(applicationRow.Id, customerRow.Id, input.RequestedAmount, "USD"));
            db.OutboxMessages.Add(new() { Id = eventId, ApplicationId = applicationRow.Id, ApplicationVersion = applicationRow.Version,
                PolicyRevisionId = revisionId, CorrelationId = correlationId, Operation = operation,
                PayloadJson = JsonSerializer.Serialize(payload, PolicyJson.Options), CreatedAtUtc = now, NextAttemptAtUtc = now });
            db.AuditEvents.AddRange(
                Audit("Application.Approved", "Application", applicationRow.Id, revisionId, eventId, correlationId, new { applicationVersion = applicationRow.Version, operation }),
                Audit("Customer." + operation, "Customer", customerRow.Id, revisionId, eventId, correlationId, new { changedFields = changed.Where(f => f != "requestedAmount").ToArray() }),
                Audit("Application." + operation, "Application", applicationRow.Id, revisionId, eventId, correlationId, new { applicationVersion = applicationRow.Version, changedFields = changed }),
                Audit("Outbox.Created", "OutboxMessage", eventId, revisionId, eventId, correlationId, new { applicationId = applicationRow.Id, applicationVersion = applicationRow.Version }));
            try { await db.SaveChangesAsync(ct); return Response(payload); }
            catch (Exception error) when (Retryable(error))
            { if (attempt == 2) throw new PolicyError(409, "SUBMISSION_CONFLICT"); }
            catch (Exception error) when (TransportFailure(error))
            {
                // No blind replay: the stable event is proof of the whole atomic commit.
                try
                {
                    await using var recovery = new AppDbContext(options);
                    var row = await recovery.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(o => o.Id == eventId, ct);
                    if (row is not null)
                    {
                        var saved = JsonSerializer.Deserialize<ApplicationEvent>(row.PayloadJson, PolicyJson.Options);
                        if (saved is not null && saved.EventId == eventId && saved.PolicyRevisionId == revisionId &&
                            saved.Application.Id == row.ApplicationId && saved.ApplicationVersion == row.ApplicationVersion &&
                            saved.Operation == row.Operation && saved.Customer.Id == saved.Application.CustomerId &&
                            saved.SchemaVersion == 1 && row.CorrelationId == correlationId &&
                            saved.Customer == payload.Customer && saved.Application == payload.Application &&
                            saved.ApplicationVersion == payload.ApplicationVersion && saved.Operation == payload.Operation)
                            return Response(saved);
                    }
                }
                catch (Exception recoveryError) when (recoveryError is not OperationCanceledException) { }
                throw new PolicyError(503, "OUTCOME_UNKNOWN");
            }
        }
        throw new PolicyError(409, "SUBMISSION_CONFLICT");
    }
    public async Task RecordDenied(Guid revisionId, Guid[] ruleIds, Guid correlationId, CancellationToken ct)
    {
        await using var db = new AppDbContext(options);
        var audit = Audit("Application.Denied", "Application", null, revisionId, null, correlationId, new { ruleIds }); audit.Outcome = "Denied";
        db.AuditEvents.Add(audit);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new PolicyError(503, "AUDIT_UNAVAILABLE"); }
    }
    private static ApprovedApplication Response(ApplicationEvent e) => new("Approved", e.Customer.Id, e.Application.Id, e.ApplicationVersion, e.Operation, e.PolicyRevisionId, e.EventId);
    private static bool Retryable(Exception error) => error is DbUpdateConcurrencyException || (error is DbUpdateException update ? update.InnerException : error) is PostgresException p &&
        (p.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure ||
         p.SqlState == PostgresErrorCodes.UniqueViolation && p.ConstraintName is "IX_Customers_Ssn" or "IX_OutboxMessages_ApplicationId_ApplicationVersion");
    private static bool TransportFailure(Exception error) => error is TimeoutException or NpgsqlException and not PostgresException ||
        error is DbUpdateException { InnerException: NpgsqlException and not PostgresException or TimeoutException };
    private static AuditEvent Audit(string action, string entity, Guid? id, Guid revision, Guid? eventId, Guid correlation, object metadata) => new()
    { Id = Guid.NewGuid(), Action = action, Component = "Api", ActorType = "Local", EntityType = entity, EntityId = id,
        PolicyRevisionId = revision, OutboxEventId = eventId, CorrelationId = correlation, Outcome = "Succeeded", MetadataJson = JsonSerializer.Serialize(metadata) };
    private static string[] ChangedFields(Customer c, LoanApplication a, Submission s, bool created) => new (string Name, bool Changed)[]
    {
        ("firstName", c.FirstName != s.FirstName), ("lastName", c.LastName != s.LastName), ("companyName", c.CompanyName != s.CompanyName),
        ("address.line1", c.AddressLine1 != s.Address.Line1), ("address.line2", c.AddressLine2 != s.Address.Line2), ("address.city", c.City != s.Address.City),
        ("address.state", c.State != s.Address.State), ("address.postalCode", c.PostalCode != s.Address.PostalCode), ("requestedAmount", a.RequestedAmount != s.RequestedAmount)
    }.Where(f => created || f.Changed).Select(f => f.Name).ToArray();
}
