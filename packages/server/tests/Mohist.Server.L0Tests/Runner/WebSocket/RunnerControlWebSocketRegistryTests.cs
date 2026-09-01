using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.WebSocket;
using Mohist.Server.Sessions.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.L0Tests.Runner.WebSocket;

public sealed class RunnerControlWebSocketRegistryTests
{
    [Fact]
    public void HandshakePreservesExactOpaqueProcessGeneration()
    {
        var query = new Microsoft.AspNetCore.Http.QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["processGeneration"] = "  opaque generation  ",
            });

        var handshake = RunnerControlHandshake.FromQuery(query);

        Assert.Equal("  opaque generation  ", handshake.ProcessGeneration);
    }

    [Fact]
    public async Task MissingProcessGenerationIsRejectedBeforeConnectionPublication()
    {
        var fixture = RegistryFixture();
        Assert.True(fixture.Registry.TryReserve(fixture.ConnectionId, out var reservation));

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => fixture.Registry.RunAsync(
            "runner-1",
            reservation,
            fixture.Socket,
            new RunnerControlHandshake(null, null, null, null, null, null, null, null, null),
            TestContext.Current.CancellationToken));

        Assert.False(fixture.Registry.IsConnected("runner-1"));
    }

    [Fact]
    public async Task StaleProcessGenerationIsRejectedBeforeConnectionPublication()
    {
        var fixture = RegistryFixture(currentGeneration: "current-generation");
        Assert.True(fixture.Registry.TryReserve(fixture.ConnectionId, out var reservation));

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => fixture.Registry.RunAsync(
            "runner-1",
            reservation,
            fixture.Socket,
            new RunnerControlHandshake(null, null, null, null, null, null, null, null, "stale-generation"),
            TestContext.Current.CancellationToken));

        Assert.False(fixture.Registry.IsConnected("runner-1"));
    }

    [Fact]
    public async Task GenerationChangedWhileInstallationWaitsRejectsWithoutFencingCurrentLease()
    {
        const string runnerId = "runner-1";
        var fixture = RegistryFixture();
        var currentId = fixture.ConnectionId;
        using var currentStop = new CancellationTokenSource();
        Assert.True(fixture.Registry.TryReserve(currentId, out var currentReservation));
        var currentRun = fixture.Registry.RunAsync(
            runnerId, currentReservation, fixture.Socket, Handshake(), currentStop.Token);
        await fixture.Registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        var replacementId = Guid.NewGuid();
        using var replacementSocket = new InstallationWebSocket(blockClose: false);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Registry.InstallationAcquiredAsync = async (_, connectionId, ct) =>
        {
            if (connectionId != replacementId) return;
            acquired.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        Assert.True(fixture.Registry.TryReserve(replacementId, out var replacementReservation));
        var replacementRun = fixture.Registry.RunAsync(
            runnerId, replacementReservation, replacementSocket, Handshake(), TestContext.Current.CancellationToken);
        await acquired.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Runner.CurrentGeneration = "replacement-generation";
        release.TrySetResult();

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => replacementRun);
        Assert.True(fixture.Registry.IsConnected(runnerId));
        Assert.Equal(WebSocketState.Open, fixture.Socket.State);

        currentStop.Cancel();
        await currentRun;
    }

    [Fact]
    public async Task ReplacementBetweenGenerationValidationAndEnqueueRejectsOldCommand()
    {
        const string runnerId = "runner-1";
        var fixture = RegistryFixture();
        using var oldStop = new CancellationTokenSource();
        Assert.True(fixture.Registry.TryReserve(fixture.ConnectionId, out var oldReservation));
        var oldRun = fixture.Registry.RunAsync(
            runnerId, oldReservation, fixture.Socket, Handshake(), oldStop.Token);
        await fixture.Registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        fixture.Registry.SessionCommandGenerationValidatedAsync = async (_, _, ct) =>
        {
            fixture.Registry.SessionCommandGenerationValidatedAsync = null;
            fixture.Runner.CurrentGeneration = "replacement-generation";
            var replacementId = Guid.NewGuid();
            using var replacementSocket = new InstallationWebSocket(blockClose: false);
            using var replacementStop = new CancellationTokenSource();
            Assert.True(fixture.Registry.TryReserve(replacementId, out var replacementReservation));
            var replacementRun = fixture.Registry.RunAsync(
                runnerId,
                replacementReservation,
                replacementSocket,
                new RunnerControlHandshake(null, null, null, null, null, null, null, null, "replacement-generation"),
                replacementStop.Token);
            await oldRun.WaitAsync(ct);
            await fixture.Registry.WaitForConnectionAsync(runnerId, ct);
            replacementStop.Cancel();
            await replacementRun;
        };

        var dispatcher = new RunnerSessionCommandDispatcher(
            fixture.Registry, NullLogger<RunnerSessionCommandDispatcher>.Instance);
        var result = await dispatcher.DispatchAsync(Command(runnerId), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SessionCommandError.Unavailable, result.Error);
        Assert.Equal(0, fixture.Socket.SendCount);
        await oldRun;
    }

    [Fact]
    public async Task GenerationChangeAfterFinalValidationMaySendOnceButRejectsOldResult()
    {
        const string runnerId = "runner-1";
        var fixture = RegistryFixture();
        fixture.Socket.RespondWithCompactSuccess = true;
        using var oldStop = new CancellationTokenSource();
        Assert.True(fixture.Registry.TryReserve(fixture.ConnectionId, out var oldReservation));
        var oldRun = fixture.Registry.RunAsync(
            runnerId, oldReservation, fixture.Socket, Handshake(), oldStop.Token);
        await fixture.Registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        var validationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Registry.SessionCommandGenerationValidatedAsync = async (_, _, ct) =>
        {
            validationReached.TrySetResult();
            await releaseValidation.Task.WaitAsync(ct);
        };
        var dispatcher = new RunnerSessionCommandDispatcher(
            fixture.Registry, NullLogger<RunnerSessionCommandDispatcher>.Instance);
        var pending = dispatcher.DispatchAsync(Command(runnerId), TestContext.Current.CancellationToken);
        await validationReached.Task.WaitAsync(TestContext.Current.CancellationToken);

        fixture.Runner.CurrentGeneration = "replacement-generation";
        releaseValidation.TrySetResult();

        var result = await pending;
        Assert.False(result.Ok);
        Assert.Equal(SessionCommandError.Unavailable, result.Error);
        Assert.Equal(1, fixture.Socket.SendCount);

        oldStop.Cancel();
        await oldRun;
    }

    [Fact]
    public async Task ReplacementWhileCommandIsInFlightMakesOldResultUnavailable()
    {
        const string runnerId = "runner-1";
        var fixture = RegistryFixture();
        using var oldStop = new CancellationTokenSource();
        Assert.True(fixture.Registry.TryReserve(fixture.ConnectionId, out var oldReservation));
        var oldRun = fixture.Registry.RunAsync(
            runnerId, oldReservation, fixture.Socket, Handshake(), oldStop.Token);
        await fixture.Registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        var dispatcher = new RunnerSessionCommandDispatcher(
            fixture.Registry, NullLogger<RunnerSessionCommandDispatcher>.Instance);
        var pending = dispatcher.DispatchAsync(Command(runnerId), TestContext.Current.CancellationToken);
        await fixture.Socket.SendStarted.WaitAsync(TestContext.Current.CancellationToken);

        fixture.Runner.CurrentGeneration = "replacement-generation";
        var replacementId = Guid.NewGuid();
        using var replacementSocket = new InstallationWebSocket(blockClose: false);
        using var replacementStop = new CancellationTokenSource();
        Assert.True(fixture.Registry.TryReserve(replacementId, out var replacementReservation));
        var replacementRun = fixture.Registry.RunAsync(
            runnerId,
            replacementReservation,
            replacementSocket,
            new RunnerControlHandshake(null, null, null, null, null, null, null, null, "replacement-generation"),
            replacementStop.Token);

        var result = await pending;
        Assert.False(result.Ok);
        Assert.Equal(SessionCommandError.Unavailable, result.Error);
        await fixture.Registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        replacementStop.Cancel();
        await replacementRun;
        await oldRun;
    }

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

    private static (RunnerControlWebSocketRegistry Registry, Guid ConnectionId, InstallationWebSocket Socket, RunnerGrainProxy Runner) RegistryFixture(
        string currentGeneration = "test-generation")
    {
        var runner = DispatchProxy.Create<IRunnerGrain, RunnerGrainProxy>();
        ((RunnerGrainProxy)(object)runner).CurrentGeneration = currentGeneration;
        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grains).Runner = runner;
        return (
            new RunnerControlWebSocketRegistry(
                new RunnerConnectionTracker(),
                grains,
                new FakeTimeProvider(),
                NullLoggerFactory.Instance),
            Guid.NewGuid(),
            new InstallationWebSocket(blockClose: false),
            (RunnerGrainProxy)(object)runner);
    }

    private static RunnerControlHandshake Handshake() => new(null, null, null, null, null, null, null, null, "test-generation");

    private static SessionCommandRequest Command(string runnerId) => new(
        "session-1",
        "opencode",
        "runtime-1",
        runnerId,
        "/workspace",
        SessionCommandKind.Compact,
        OperationId: "operation-1",
        ProcessGeneration: "test-generation");

    private class RunnerGrainProxy : DispatchProxy
    {
        public string CurrentGeneration { get; set; } = "test-generation";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IRunnerGrain.IsCurrentProcessGenerationAsync) => Task.FromResult(
                    string.Equals(CurrentGeneration, (string?)args![0], StringComparison.Ordinal)),
                nameof(IRunnerGrain.UpdateRuntimeIdentityAsync) => Task.CompletedTask,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
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
        private readonly TaskCompletionSource<byte[]> _receive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _sendCount;

        public Task CloseStarted => _closeStarted.Task;
        public Task SendStarted => _sendStarted.Task;
        public int SendCount => Volatile.Read(ref _sendCount);
        public bool RespondWithCompactSuccess { get; set; }
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
            CancellationToken cancellationToken)
        {
            var payload = await _receive.Task.WaitAsync(cancellationToken);
            payload.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            _sendStarted.TrySetResult();
            if (RespondWithCompactSuccess)
            {
                using var request = JsonDocument.Parse(buffer.AsMemory());
                var id = request.RootElement.GetProperty("id").GetString();
                _receive.TrySetResult(System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"ok\":true}}}}"));
            }
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _closeRelease.TrySetResult();
        }
    }
}
