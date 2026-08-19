using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.WebSocket;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.WebSocket;

public sealed class RunnerControlWebSocketRegistryTests
{
    [Fact]
    public async Task ReplacementLeaseFencesStaleTrafficBeforeOldCloseCompletes()
    {
        const string runnerId = "runner-1";
        var tracker = new RunnerConnectionTracker();
        var runner = DispatchProxy.Create<IRunnerGrain, RunnerGrainProxy>();
        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grains).Runner = runner;
        var registry = new RunnerControlWebSocketRegistry(
            tracker, grains, new FakeTimeProvider(), NullLoggerFactory.Instance);
        var oldConnectionId = Guid.NewGuid();
        var newConnectionId = Guid.NewGuid();
        using var oldStop = new CancellationTokenSource();
        using var newStop = new CancellationTokenSource();
        using var oldSocket = new InstallationWebSocket(blockClose: true);
        using var newSocket = new InstallationWebSocket(blockClose: false);

        Assert.True(registry.TryReserve(oldConnectionId, out var oldReservation));
        var oldRun = registry.RunAsync(
            runnerId, oldReservation, oldSocket, Handshake(), oldStop.Token);
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        Assert.Equal(oldConnectionId.ToString("D"), tracker.GetConnectionId(runnerId));

        Assert.True(registry.TryReserve(newConnectionId, out var newReservation));
        var newRun = registry.RunAsync(
            runnerId, newReservation, newSocket, Handshake(), newStop.Token);
        await oldSocket.CloseStarted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(newConnectionId.ToString("D"), tracker.GetConnectionId(runnerId));
        Assert.False(tracker.Matches(runnerId, oldConnectionId.ToString("D")));
        Assert.Null(tracker.ApplyPollAdmission(
            runnerId, new RunnerPollRequest([], [], ConnectionId: oldConnectionId.ToString("D"))).ConnectionGeneration);
        Assert.False(registry.IsConnected(runnerId));

        oldSocket.ReleaseClose();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        Assert.True(registry.IsConnected(runnerId));

        newStop.Cancel();
        await newRun;
        await oldRun;
    }

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

    private static RunnerControlHandshake Handshake() => new(null, null, null, null, null, null, null, null);

    private class RunnerGrainProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IRunnerGrain.UpdateRuntimeIdentityAsync)
                ? Task.CompletedTask
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public IRunnerGrain Runner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IRunnerGrain))
                return Runner;
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class InstallationWebSocket(bool blockClose) : System.Net.WebSockets.WebSocket
    {
        private readonly TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebSocketReceiveResult> _receive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public Task CloseStarted => _closeStarted.Task;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void ReleaseClose() => _closeRelease.TrySetResult();
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override async Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStarted.TrySetResult();
            if (blockClose) await _closeRelease.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => await _receive.Task.WaitAsync(cancellationToken);

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _closeRelease.TrySetResult();
        }
    }
}
