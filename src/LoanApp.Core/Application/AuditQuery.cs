using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoanApp.Core.Application;

public sealed record AuditItem(Guid Id, DateTimeOffset OccurredAtUtc, string Component, string Action, Guid CorrelationId,
    string ActorType, string? ActorRef, string EntityType, Guid? EntityId, string Outcome, string? ReasonCode,
    Guid? PolicyRevisionId, Guid? OutboxEventId, Dictionary<string, JsonElement> Metadata);
public sealed record AuditPage(AuditItem[] Items, string? NextCursor);
public sealed class AuditRow
{
    public Guid Id { get; set; } public DateTimeOffset OccurredAtUtc { get; set; }
    public string Component { get; set; } = ""; public string Action { get; set; } = "";
    public Guid CorrelationId { get; set; } public string ActorType { get; set; } = "";
    public string? ActorRef { get; set; } public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; } public string Outcome { get; set; } = "";
    public string? ReasonCode { get; set; } public Guid? PolicyRevisionId { get; set; }
    public Guid? OutboxEventId { get; set; } public string MetadataJson { get; set; } = "{}";
    public AuditItem Safe() => new(Id, OccurredAtUtc, Component, Action, CorrelationId, ActorType,
        Guid.TryParse(ActorRef, out var actor) ? actor.ToString() : null, EntityType, EntityId, Outcome, ReasonCode,
        PolicyRevisionId, OutboxEventId, SafeMetadata(MetadataJson));
    public static Dictionary<string, JsonElement> SafeMetadata(string json)
    {
        var result = new Dictionary<string, JsonElement>(); if (Encoding.UTF8.GetByteCount(json) > 8192) return result;
        try
        {
            using var doc = JsonDocument.Parse(json); if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                var value = field.Value;
                var valid = field.Name switch
                {
                    "applicationVersion" or "attemptCount" or "rulesCount" or "blacklistCount" or "previousDraftVersion" or "draftVersion" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0,
                    "applicationId" or "customerId" => value.ValueKind == JsonValueKind.String && value.TryGetGuid(out _),
                    "operation" => value.ValueKind == JsonValueKind.String && value.GetString() is "Created" or "Updated",
                    "ruleIds" or "affectedRuleIds" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 50 && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && v.TryGetGuid(out _)),
                    "changedFields" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && v.GetString() is "firstName" or "lastName" or "address.line1" or "address.line2" or "address.city" or "address.state" or "address.postalCode" or "companyName" or "requestedAmount"),
                    _ => false
                };
                if (valid) result[field.Name] = value.Clone();
            }
        }
        catch (JsonException) { }
        return result;
    }
}

public sealed class AuditQuery
{
    public int Limit { get; private init; } public DateTimeOffset? FromUtc { get; private init; } public DateTimeOffset? ToUtc { get; private init; }
    public string? Action { get; private init; } public string? EntityType { get; private init; }
    public Guid? EntityId { get; private init; } public Guid? CorrelationId { get; private init; }
    public string? Cursor { get; private init; } private string Fingerprint { get; init; } = "";
    private AuditCursor? Position { get; set; }
    private sealed record AuditCursor(DateTimeOffset Time, Guid Id, string Fingerprint);
    public static AuditQuery Parse(IReadOnlyDictionary<string, string> values, string origin)
    {
        string? Get(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        Guid? Id(string key) => Get(key) is not { } value ? null : Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new PolicyError(400, "INVALID_FILTER");
        DateTimeOffset? Date(string key)
        {
            var value = Get(key); if (value is null) return null;
            if (!(value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) || !value.Contains('T') ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Offset != TimeSpan.Zero)
                throw new PolicyError(400, "INVALID_DATE"); return date;
        }
        string? Token(string key, int max)
        {
            var value = Get(key); if (value is not null && (value.Length > max || !Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_.]*$"))) throw new PolicyError(400, "INVALID_FILTER"); return value;
        }
        var limit = 20; if (Get("limit") is { } raw && (!int.TryParse(raw, out limit) || limit is < 1 or > 100)) throw new PolicyError(400, "INVALID_LIMIT");
        var from = Date("fromUtc"); var to = Date("toUtc"); if (from >= to) throw new PolicyError(400, "INVALID_RANGE");
        var action = Token("action", 80); var entityType = Token("entityType", 40); var entityId = Id("entityId"); var correlation = Id("correlationId");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { origin, from, to, action, entityType, entityId, correlation }))));
        var query = new AuditQuery { Limit = limit, FromUtc = from, ToUtc = to, Action = action, EntityType = entityType, EntityId = entityId, CorrelationId = correlation, Cursor = Get("cursor"), Fingerprint = fingerprint };
        if (query.Cursor is { } cursor)
        {
            try
            {
                if (cursor.Length > 1024) throw new FormatException();
                query.Position = JsonSerializer.Deserialize<AuditCursor>(Convert.FromBase64String(cursor));
                if (query.Position is not { } pos || pos.Id == Guid.Empty || pos.Time.Offset != TimeSpan.Zero || pos.Time == default || pos.Fingerprint != fingerprint) throw new FormatException();
            }
            catch (Exception e) when (e is JsonException or FormatException) { throw new PolicyError(400, "INVALID_CURSOR"); }
        }
        return query;
    }
    public IQueryable<AuditRow> Apply(IQueryable<AuditRow> rows)
    {
        if (FromUtc is { } from) rows = rows.Where(r => r.OccurredAtUtc >= from);
        if (ToUtc is { } to) rows = rows.Where(r => r.OccurredAtUtc < to);
        if (Action is { } action) rows = rows.Where(r => r.Action == action);
        if (EntityType is { } type) rows = rows.Where(r => r.EntityType == type);
        if (EntityId is { } entity) rows = rows.Where(r => r.EntityId == entity);
        if (CorrelationId is { } correlation) rows = rows.Where(r => r.CorrelationId == correlation);
        if (Position is { } p) rows = rows.Where(r => r.OccurredAtUtc < p.Time || r.OccurredAtUtc == p.Time && r.Id.CompareTo(p.Id) < 0);
        return rows.OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.Id).Take(Limit + 1);
    }
    public AuditPage Page(AuditRow[] rows)
    {
        var selected = rows.Take(Limit).ToArray(); var last = selected.LastOrDefault();
        return new(selected.Select(r => r.Safe()).ToArray(), rows.Length > Limit && last is not null ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new AuditCursor(last.OccurredAtUtc, last.Id, Fingerprint))) : null);
    }
}
