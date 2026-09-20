namespace LoanApp.Api.Infrastructure.Persistence;

public sealed class Customer
{
    public Guid Id { get; set; }
    public required string Ssn { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string City { get; set; }
    public required string State { get; set; }
    public required string PostalCode { get; set; }
    public required string CompanyName { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class LoanApplication
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal RequestedAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public long Version { get; set; }
    public Guid LastPolicyRevisionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public long ApplicationVersion { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public Guid CorrelationId { get; set; }
    public required string Operation { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public required string PayloadJson { get; set; }
    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
}
public sealed class PolicyHead
{
    public int Id { get; set; } = 1;
    public Guid? ActiveRevisionId { get; set; }
    public Guid? DraftRevisionId { get; set; }
    public Guid? BaselineRevisionId { get; set; }
    public long Version { get; set; } = 1;
}
public sealed class PolicyRevision
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public required string DocumentJson { get; set; }
    public long DraftVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}
public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string Component { get; set; }
    public required string Action { get; set; }
    public Guid CorrelationId { get; set; }
    public required string ActorType { get; set; }
    public string? ActorRef { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public required string Outcome { get; set; }
    public string? ReasonCode { get; set; }
    public Guid? PolicyRevisionId { get; set; }
    public Guid? OutboxEventId { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
