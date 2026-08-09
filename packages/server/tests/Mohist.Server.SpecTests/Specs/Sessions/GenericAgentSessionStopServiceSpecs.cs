using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public sealed class GenericAgentSessionStopServiceSpecs : GenericAgentSessionStopTestSupport
{
    public GenericAgentSessionStopServiceSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Stop_QueuedTurnReturnsQueuedWithoutContactingRunner()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForStopAsync();
        var hub = Hub();
        hub.Clear();

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.Queued, result.Kind);
        Assert.Equal(AgentTurnStatus.Queued, result.Status);
        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_UnknownTurnReturnsNotFoundWithoutContactingRunner()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var hub = Hub();
        hub.Clear();

        var result = await StopAsync(project.Id, sessionId, "missing-turn", hub: hub);

        Assert.Equal(TurnControlResultKind.NotFound, result.Kind);
        Assert.Null(result.Status);
        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_TerminalTurnReturnsAlreadyEndedWithoutContactingRunner()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForStopAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        Assert.True((await session.CancelQueuedTurnAsync(turnId)).Cancelled);
        var hub = Hub();
        hub.Clear();

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.AlreadyEnded, result.Kind);
        Assert.Equal(AgentTurnStatus.Cancelled, result.Status);
        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_UnknownTerminalTurnReturnsAlreadyEndedWithoutContactingRunner()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Unknown, null);
        var hub = Hub();
        hub.Clear();

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.AlreadyEnded, result.Kind);
        Assert.Equal(AgentTurnStatus.Unknown, result.Status);
        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_ExecutingFollowupSendsGenericBindingAndCompletesTheTurn()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var info = await session.GetAsync() ?? throw new InvalidOperationException("session was not persisted");
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.Stopped, result.Kind);
        Assert.Equal(AgentTurnStatus.Executing, result.Status);
        var invocation = Assert.Single(hub.Invocations);
        var payload = Payload(invocation);
        var target = payload.GetProperty("target");
        Assert.Equal("generic", target.GetProperty("kind").GetString());
        Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
        Assert.Equal(sessionId, target.GetProperty("sessionId").GetString());
        Assert.Equal(turnId, payload.GetProperty("turnId").GetString());
        var binding = target.GetProperty("binding");
        Assert.Equal(info.Runtime, binding.GetProperty("runtime").GetString());
        Assert.Equal(info.RunnerId, binding.GetProperty("runnerId").GetString());
        Assert.Equal(info.WorkDir, binding.GetProperty("workDir").GetString());
        Assert.Equal(info.AgentSessionId, binding.GetProperty("runtimeSessionId").GetString());
        Assert.Equal(AgentTurnStatus.Completed, Assert.Single(await session.ListTurnsAsync()).Status);

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_NotCancellableCompletesTheClaimButLeavesTheTurnExecuting()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("not-cancellable"));

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.NotCancellable, result.Kind);
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await session.ListTurnsAsync()).Status);
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_RunnerUnavailableBeforeDispatchAbandonsTheClaim()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();
        UnregisterRunnerConnection();

        try
        {
            var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

            Assert.Equal(TurnControlResultKind.RunnerUnavailable, result.Kind);
            Assert.False(result.DispatchStarted);
            Assert.Empty(hub.Invocations);
            var reservation = await session.BeginFollowupAsync();
            Assert.False(reservation.StartsIdleTurn);
            await session.AbandonFollowupAsync(reservation.OperationId!);
        }
        finally
        {
            RegisterRunnerConnection();
        }

        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_DispatchExceptionRetainsClaimUntilOwnerCompletesIt()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponseFactory("CancelAgentSession", _ =>
            throw new InvalidOperationException("dispatch failed"));

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.RunnerUnavailable, result.Kind);
        Assert.True(result.DispatchStarted);
        var operationId = ReadOperationId(Assert.Single(hub.Invocations));
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);

        await session.CompleteTurnStopAsync(turnId, operationId);
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_NullReplyRetainsClaimUntilOwnerCompletesIt()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.RunnerUnavailable, result.Kind);
        Assert.True(result.DispatchStarted);
        var operationId = ReadOperationId(Assert.Single(hub.Invocations));
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);

        await session.CompleteTurnStopAsync(turnId, operationId);
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_CancellationAfterDispatchRetainsClaimAndRethrowsCancellation()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();
        using var cancellation = new CancellationTokenSource();
        hub.SetInvocationResponseFactory("CancelAgentSession", _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            StopAsync(project.Id, sessionId, turnId, cancellation.Token, hub: hub));

        var operationId = ReadOperationId(Assert.Single(hub.Invocations));
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);
        await session.CompleteTurnStopAsync(turnId, operationId);
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_UnknownFollowupMarksTurnUnknownAndRetainsClaimForRuntimeFact()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("unknown", true));

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.Unknown, result.Kind);
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(await session.ListTurnsAsync()).Status);
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);

        var operationId = ReadOperationId(Assert.Single(hub.Invocations));
        await session.CompleteTurnStopAsync(turnId, operationId);
        await Assert.ThrowsAsync<SessionActivityUnknownException>(session.BeginFollowupAsync);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_TerminalTurnBeforeReplyClearsClaimAfterDispatch()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponseFactory(
            "CancelAgentSession",
            _ => CompleteTargetBeforeReplyAsync(session, turnId));

        var result = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.Stopped, result.Kind);
        Assert.Equal(AgentTurnStatus.Completed, Assert.Single(await session.ListTurnsAsync()).Status);
        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_ReplyLossRetryUsesTheSameDurableOperationId()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();

        var first = await StopAsync(project.Id, sessionId, turnId, hub: hub);

        Assert.Equal(TurnControlResultKind.RunnerUnavailable, first.Kind);
        var firstOperationId = ReadOperationId(Assert.Single(hub.Invocations));
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);

        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("not-cancellable"));
        var second = await StopAsync(
            project.Id,
            sessionId,
            turnId,
            expectedOperationId: firstOperationId,
            hub: hub);

        Assert.Equal(TurnControlResultKind.NotCancellable, second.Kind);
        var invocations = hub.Invocations.ToArray();
        Assert.Equal(2, invocations.Length);
        Assert.Equal(firstOperationId, ReadOperationId(invocations[1]));
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_SameTurnConcurrencySharesOperationIdentityAndLeavesNoClaim()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var initialClaim = await session.ClaimTurnStopAsync(turnId, "same-turn-operation");
        Assert.True(initialClaim.CanDispatch);
        Assert.Equal("same-turn-operation", initialClaim.OperationId);

        var firstHub = new RecordingRunnerHubContext();
        var secondHub = new RecordingRunnerHubContext();
        var reply = NewReplySignal();
        var secondInvocation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationNumber = 0;
        object? ReturnUnknownReply(IReadOnlyList<object?> _)
        {
            var number = Interlocked.Increment(ref invocationNumber);
            if (number == 2)
                secondInvocation.TrySetResult(true);
            return reply.Task;
        }

        firstHub.SetInvocationResponseFactory("CancelAgentSession", ReturnUnknownReply);
        secondHub.SetInvocationResponseFactory("CancelAgentSession", ReturnUnknownReply);
        Task<TurnControlResult>? first = null;
        Task<TurnControlResult>? second = null;
        try
        {
            first = StopAsync(
                project.Id,
                sessionId,
                turnId,
                expectedOperationId: initialClaim.OperationId,
                hub: firstHub);
            second = StopAsync(
                project.Id,
                sessionId,
                turnId,
                expectedOperationId: initialClaim.OperationId,
                hub: secondHub);

            var dispatchOrSettlement = await Task.WhenAny(
                secondInvocation.Task,
                first!,
                second!);
            Assert.Same(secondInvocation.Task, dispatchOrSettlement);
            Assert.True(secondInvocation.Task.IsCompleted);
            Assert.Equal(2, invocationNumber);
            Assert.True(reply.TrySetResult(new RunnerStopReply("unknown", true)));
            var results = await Task.WhenAll(first!, second!);

            Assert.All(results, result => Assert.Equal(TurnControlResultKind.Unknown, result.Kind));
            Assert.Equal(
                initialClaim.OperationId,
                ReadOperationId(Assert.Single(firstHub.Invocations)));
            Assert.Equal(
                initialClaim.OperationId,
                ReadOperationId(Assert.Single(secondHub.Invocations)));
            Assert.True(reply.Task.IsCompleted);

            await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);
            await session.CompleteTurnStopAsync(turnId, initialClaim.OperationId!);
            await Assert.ThrowsAsync<SessionActivityUnknownException>(session.BeginFollowupAsync);
            await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
        }
        finally
        {
            reply.TrySetResult(new RunnerStopReply("unknown", true));
            var pendingOperations = new List<Task>(2);
            if (first is not null)
                pendingOperations.Add(first);
            if (second is not null)
                pendingOperations.Add(second);
            if (pendingOperations.Count > 0)
                await Task.WhenAll(pendingOperations);
        }
    }

    private async Task<TurnControlResult> StopAsync(
        string projectId,
        string sessionId,
        string turnId,
        CancellationToken ct = default,
        string? expectedOperationId = null,
        RecordingRunnerHubContext? hub = null)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await session.GetAsync() ?? throw new InvalidOperationException("session was not persisted");
        var target = new SessionCancelTarget(
            info.RunnerId ?? string.Empty,
            info.Id,
            "agent-launch",
            null,
            null,
            info.Runtime,
            info.AgentSessionId,
            info.WorkDir);
        return await AgentSessionTurnControlOperations.StopAsync(
            projectId,
            _fixture.Grains,
            hub ?? Hub(),
            RunnerConnections,
            target,
            turnId,
            ct,
            expectedOperationId);
    }

    private RecordingRunnerHubContext Hub() =>
        _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
        ?? throw new InvalidOperationException("Recording runner hub context was not registered.");

    private static JsonElement Payload(RecordedRunnerHubInvocation invocation) =>
        JsonSerializer.SerializeToElement(invocation.Arguments.Single());

    private static string ReadOperationId(RecordedRunnerHubInvocation invocation) =>
        Payload(invocation).GetProperty("operationId").GetString()
        ?? throw new InvalidOperationException("stop invocation did not contain an operation id");

    private static async Task<RunnerStopReply> CompleteTargetBeforeReplyAsync(
        IAgentSessionGrain session,
        string turnId)
    {
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Completed, null);
        return new RunnerStopReply("stopped");
    }

    private static TaskCompletionSource<RunnerStopReply?> NewReplySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
