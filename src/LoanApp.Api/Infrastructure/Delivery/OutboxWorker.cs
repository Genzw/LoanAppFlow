namespace LoanApp.Api.Infrastructure.Delivery;

public sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger, WorkerWakeup wakeup, WorkerCadence cadence) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            double? nextDue = null;
            var processed = 0;
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    using var scope = scopes.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
                    var message = await store.Claim(stoppingToken); if (message is null) break;
                    var client = scope.ServiceProvider.GetRequiredService<DeliveryClient>();
                    var outcome = await client.Send(message, stoppingToken);
                    var recorded = await store.Complete(message, outcome, stoppingToken);
                    processed++;
                    cadence.Reset();
                    logger.LogInformation("Delivery {Component} {Action} {EventId} {ApplicationVersion} {CorrelationId} {Outcome} {Recorded}",
                        "Api", "OutboxDelivery", message.Id, message.ApplicationVersion, message.CorrelationId, outcome.Kind, recorded);
                }
                if (processed == 20) continue;
                using var scheduleScope = scopes.CreateScope();
                nextDue = await scheduleScope.ServiceProvider.GetRequiredService<OutboxStore>().NextDueSeconds(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Worker iteration failed {Component} {Action} {ErrorType}", "Api", "OutboxDelivery", e.GetType().Name); }
            try { if (await wakeup.Wait(cadence.Next(nextDue), stoppingToken)) cadence.Reset(); }
            catch (OperationCanceledException) { break; }
        }
    }
}
