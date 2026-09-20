using LoanApp.Core.Rules;

namespace LoanApp.Api.Endpoints;

public sealed record CreateDraftInput(Guid? SourceRevisionId);
public sealed record RulesInput(RuleDefinition[] Rules);
public sealed record BlacklistInput(string Ssn);
public sealed record PublishInput(long? ExpectedHeadVersion = null);
public sealed record HeadResponse(long HeadVersion, Guid? ActiveRevisionId, Guid? DraftRevisionId, Guid? BaselineRevisionId);
public sealed record MaskedBlacklistEntry(Guid Id, string MaskedSsn);
public sealed record RevisionResponse(Guid Id, string Kind, Guid? BaseRevisionId, long DraftVersion,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc, int SchemaVersion,
    RuleDefinition[] Rules, MaskedBlacklistEntry[] Blacklist);
public sealed record DraftResponse(RevisionResponse Revision, long HeadVersion);
public sealed record HistoryItem(Guid Id, Guid? BaseRevisionId, DateTimeOffset? PublishedAtUtc, bool IsActive);
public sealed record HistoryResponse(HistoryItem[] Items, string? NextCursor);
public sealed record SimulationResponse(string Decision, Guid PolicyRevisionId, long DraftVersion,
    DenialReason[] Reasons, RuleTrace[] Trace);
public sealed record CatalogLimits(int Rules, int ConditionsPerRule, int Blacklist, int PolicyBytes);
public sealed record CatalogResponse(int SchemaVersion, IReadOnlyList<FieldDefinition> Fields, CatalogLimits Limits);
public sealed record ApiProblemResponse(string Type, string Title, int Status, string Code, string TraceId,
    IReadOnlyDictionary<string, string[]>? Errors);
