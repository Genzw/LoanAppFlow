namespace LoanApp.Api.Infrastructure.Delivery;

// Latency hint only: PostgreSQL remains the durable source of work.
public sealed class WorkerWakeup : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    public void Notify()
    {
        try { signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }
    public Task<bool> Wait(TimeSpan delay, CancellationToken ct) => signal.WaitAsync(delay, ct);
    public void Dispose() => signal.Dispose();
}

public sealed class WorkerCadence(bool cloud)
{
    private int idleCycles;
    public void Reset() => idleCycles = 0;
    public TimeSpan Next(double? nextDueSeconds)
    {
        var idle = cloud ? idleCycles switch { 0 => 5, 1 => 30, _ => 600 } : 1;
        idleCycles = Math.Min(idleCycles + 1, 2);
        return TimeSpan.FromSeconds(nextDueSeconds is { } due ? Math.Min(idle, Math.Max(0.25, due)) : idle);
    }
}
