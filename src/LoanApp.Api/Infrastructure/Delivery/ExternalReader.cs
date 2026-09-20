using System.Text.Json;
using System.Text.RegularExpressions;
using LoanApp.Core.Application;

namespace LoanApp.Api.Infrastructure.Delivery;

public sealed record ExternalPage<T>(T[] Items, int TotalCount, string? NextCursor);
public sealed record ExternalApplication(Guid ApplicationId, Guid CustomerId, long Version, DateTimeOffset UpdatedAtUtc,
    string CompanyName, string State, decimal RequestedAmount, string MaskedSsn);
public sealed record ExternalReceipt(Guid EventId, Guid ApplicationId, long ApplicationVersion, string Operation, DateTimeOffset ReceivedAtUtc);

public sealed class ExternalReader(HttpClient http)
{
    public async Task<AuditPage> ReadAudit(IReadOnlyDictionary<string, string> filters, int limit, Guid correlation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var keys = new[] { "fromUtc", "toUtc", "action", "entityType", "entityId", "correlationId", "cursor", "limit" };
            var path = "admin/audit?" + string.Join("&", keys.Where(filters.ContainsKey).Select(k => k + "=" + Uri.EscapeDataString(filters[k])));
            using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Add("X-Correlation-Id", correlation.ToString());
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json") throw new JsonException();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            { if (buffer.Length + read > 1048576) throw new JsonException(); buffer.Write(bytes, 0, read); }
            var page = JsonSerializer.Deserialize<AuditPage>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true }) ?? throw new JsonException();
            if (page.Items is null || page.Items.Length > limit) throw new JsonException();
            if (page.NextCursor is { } cursor) { var next = filters.ToDictionary(p => p.Key, p => p.Value); next["cursor"] = cursor; AuditQuery.Parse(next, "Mock"); }
            foreach (var item in page.Items)
                if (item is null || item.Id == Guid.Empty || item.CorrelationId == Guid.Empty || item.Component != "Mock" || item.ActorType != "Worker" ||
                    item.Action is not ("ExternalApplication.Created" or "ExternalApplication.Updated") || item.EntityType != "ExternalApplication" || item.Outcome != "Succeeded" || item.Metadata is null) throw new JsonException();
            return page with { Items = page.Items.Select(i => i with { ActorRef = Guid.TryParse(i.ActorRef, out var actor) ? actor.ToString() : null,
                ReasonCode = null, Metadata = AuditRow.SafeMetadata(JsonSerializer.Serialize(i.Metadata)) }).ToArray() };
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or PolicyError || e is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new PolicyError(503, "EXTERNAL_UNAVAILABLE"); }
    }
    public async Task<ExternalPage<T>> Read<T>(string resource, int limit, Guid? cursor, Guid correlation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var path = $"admin/{resource}?limit={limit}" + (cursor is { } id ? "&cursor=" + Uri.EscapeDataString(Convert.ToBase64String(id.ToByteArray())) : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Correlation-Id", correlation.ToString());
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") throw new JsonException();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            { if (buffer.Length + read > 262144) throw new JsonException(); buffer.Write(bytes, 0, read); }
            var page = JsonSerializer.Deserialize<ExternalPage<T>>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { RespectRequiredConstructorParameters = true }) ?? throw new JsonException();
            if (page.Items is null || page.Items.Length > limit || page.TotalCount < page.Items.Length) throw new JsonException();
            if (page.NextCursor is not null && new Guid(Convert.FromBase64String(page.NextCursor)) == Guid.Empty) throw new JsonException();
            foreach (var item in page.Items)
            {
                if (item is ExternalApplication a && (a.ApplicationId == Guid.Empty || a.CustomerId == Guid.Empty || a.Version < 1 ||
                    a.MaskedSsn is null || !Regex.IsMatch(a.MaskedSsn, @"^\*\*\*-\*\*-\d{4}$") || a.CompanyName is null || a.State is null)) throw new JsonException();
                if (item is ExternalReceipt r && (r.EventId == Guid.Empty || r.ApplicationId == Guid.Empty || r.ApplicationVersion < 1 || r.Operation is not ("Created" or "Updated"))) throw new JsonException();
                if (item is null) throw new JsonException();
            }
            return page;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or FormatException or ArgumentException || e is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new PolicyError(503, "EXTERNAL_UNAVAILABLE"); }
    }
}
