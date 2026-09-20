using LoanApp.Api.Infrastructure.Delivery;
using Xunit;
namespace LoanApp.IntegrationTests;
public sealed class WorkerCadenceTests
{
    [Fact]
    public void Cloud_idle_progresses_but_retry_and_lease_deadlines_take_precedence()
    {
        var cadence = new WorkerCadence(true);
        Assert.Equal(TimeSpan.FromSeconds(5), cadence.Next(null));
        Assert.Equal(TimeSpan.FromSeconds(30), cadence.Next(null));
        Assert.Equal(TimeSpan.FromSeconds(600), cadence.Next(null));
        Assert.Equal(TimeSpan.FromSeconds(600), cadence.Next(null));
        Assert.Equal(TimeSpan.FromSeconds(2), cadence.Next(2));
        Assert.Equal(TimeSpan.FromSeconds(45), cadence.Next(45));
        Assert.Equal(TimeSpan.FromMilliseconds(250), cadence.Next(-1));
        cadence.Reset(); Assert.Equal(TimeSpan.FromSeconds(5), cadence.Next(null));
        var local = new WorkerCadence(false);
        for (var i = 0; i < 5; i++) Assert.Equal(TimeSpan.FromSeconds(1), local.Next(null));
    }
    [Fact]
    public async Task Wakeup_keeps_pre_wait_signal_coalesces_bursts_and_cancels_cleanly()
    {
        using var wakeup = new WorkerWakeup();
        Parallel.For(0, 100, _ => wakeup.Notify());
        Assert.True(await wakeup.Wait(TimeSpan.Zero, default));
        Assert.False(await wakeup.Wait(TimeSpan.Zero, default));
        var waiting = wakeup.Wait(TimeSpan.FromMinutes(10), default); wakeup.Notify(); Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        using var stop = new CancellationTokenSource(); var canceled = wakeup.Wait(TimeSpan.FromMinutes(10), stop.Token); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        wakeup.Notify(); Assert.True(await wakeup.Wait(TimeSpan.Zero, default));
        wakeup.Dispose(); wakeup.Notify(); // A shutdown hint cannot turn a confirmed commit into an error.
    }
}
