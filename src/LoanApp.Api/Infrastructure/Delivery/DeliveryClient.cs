using System.Net;
using System.Text;
using System.Text.Json;
using LoanApp.Api.Infrastructure.Persistence;
using LoanApp.Core.Application;
using LoanApp.Core.Rules;

namespace LoanApp.Api.Infrastructure.Delivery;

public sealed class DeliveryClient(HttpClient http)
{
    public async Task<DeliveryOutcome> Send(OutboxMessage message, CancellationToken ct)
    {
        ApplicationEvent payload;
        try
        {
            payload = EventContract.Normalize(JsonSerializer.Deserialize<ApplicationEvent>(message.PayloadJson, PolicyJson.Options)!);
            if (payload.EventId != message.Id || payload.Application.Id != message.ApplicationId || payload.ApplicationVersion != message.ApplicationVersion ||
                payload.PolicyRevisionId != message.PolicyRevisionId || payload.Operation != message.Operation || payload.SchemaVersion != message.SchemaVersion)
                return new("Failed", "EVENT_INVALID");
        }
        catch (Exception e) when (e is JsonException or LoanApp.Core.Domain.ValidationFailure or NullReferenceException) { return new("Failed", "EVENT_INVALID"); }
        using var request = new HttpRequestMessage(payload.Operation == "Created" ? HttpMethod.Post : HttpMethod.Put,
            payload.Operation == "Created" ? "applications" : $"applications/{payload.Application.Id}");
        request.Content = new StringContent(JsonSerializer.Serialize(payload, PolicyJson.Options), Encoding.UTF8, "application/json");
        request.Headers.Add("X-Correlation-Id", message.CorrelationId.ToString());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var code = (int)response.StatusCode;
            if (code is 408 or 429 || code >= 500) return new("Retry", "HTTP_TRANSIENT");
            if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json")) return new("Retry", "UPSTREAM_UNAVAILABLE");
            if (response.StatusCode != HttpStatusCode.OK) return new("Failed", "HTTP_REJECTED");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); using var buffer = new MemoryStream(); var bytes = new byte[2048]; int read;
            while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            { if (buffer.Length + read > 16384) return new("Failed", "RECEIPT_INVALID"); buffer.Write(bytes, 0, read); }
            var receipt = JsonSerializer.Deserialize<DeliveryReceipt>(buffer.ToArray(), PolicyJson.Options);
            return receipt is not null && receipt.EventId == payload.EventId && receipt.ApplicationId == payload.Application.Id && receipt.ApplicationVersion == payload.ApplicationVersion
                ? new("Delivered") : new("Failed", "RECEIPT_INVALID");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("Retry", "HTTP_TIMEOUT"); }
        catch (HttpRequestException) { return new("Retry", "HTTP_UNAVAILABLE"); }
        catch (IOException) { return new("Retry", "HTTP_UNAVAILABLE"); }
        catch (JsonException) { return new("Failed", "RECEIPT_INVALID"); }
    }
}
