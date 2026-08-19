using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Runner.Services.WebSocket;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.WebSocket;

public sealed class RunnerControlWebSocketRegistryTests
{
    [Fact]
    public void StaleReleaseCannotRemoveNewReservation()
    {
        var registry = new RunnerControlWebSocketRegistry(
            new RunnerConnectionTracker(), null!, new FakeTimeProvider(), NullLoggerFactory.Instance);
        var connectionId = Guid.NewGuid();

        Assert.True(registry.TryReserve(connectionId, out var oldReservation));
        registry.ReleaseReservation(oldReservation);
        Assert.True(registry.TryReserve(connectionId, out var newReservation));

        registry.ReleaseReservation(oldReservation);

        Assert.False(registry.TryReserve(connectionId, out _));
        registry.ReleaseReservation(newReservation);
        Assert.True(registry.TryReserve(connectionId, out var finalReservation));
        registry.ReleaseReservation(finalReservation);
    }

    [Fact]
    public async Task SameRunnerSerializesAndCancelledWaiterDoesNotLeakEntry()
    {
        var gate = new RunnerControlInstallationGate();
        using var holder = await gate.AcquireAsync("runner-1", null, TestContext.Current.CancellationToken);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var waiter = gate.AcquireAsync("runner-1", waiting.SetResult, cancelled.Token);
        await waiting.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, gate.Count);

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(1, gate.Count);

        holder.Dispose();
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public async Task DifferentRunnersAcquireIndependently()
    {
        var gate = new RunnerControlInstallationGate();
        using var first = await gate.AcquireAsync("runner-1", null, TestContext.Current.CancellationToken);

        var second = await gate.AcquireAsync("runner-2", null, TestContext.Current.CancellationToken);

        Assert.Equal(2, gate.Count);
        second.Dispose();
        Assert.Equal(1, gate.Count);
    }
}
