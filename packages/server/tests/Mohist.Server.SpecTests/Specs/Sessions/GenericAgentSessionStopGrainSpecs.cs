using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class GenericAgentSessionStopGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public GenericAgentSessionStopGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task ClaimTurnStop_QueuedTerminalUnknownAndMissingTurnsDoNotCreateClaims()
    {
        var queued = await CreateSessionAsync("project-queued", "turn-queued");
        var terminal = await CreateSessionAsync("project-terminal", "turn-terminal");
        await terminal.MarkTurnTerminalAsync("turn-terminal", AgentTurnStatus.Completed, null);
        var unknown = await CreateSessionAsync("project-unknown", "turn-unknown");
        await unknown.MarkTurnTerminalAsync("turn-unknown", AgentTurnStatus.Unknown, null);

        var queuedClaim = await queued.ClaimTurnStopAsync("turn-queued");
        var terminalClaim = await terminal.ClaimTurnStopAsync("turn-terminal");
        var unknownClaim = await unknown.ClaimTurnStopAsync("turn-unknown");
        var missingClaim = await queued.ClaimTurnStopAsync("missing-turn");

        Assert.False(queuedClaim.CanDispatch);
        Assert.Equal(AgentTurnControlClassification.Queued, queuedClaim.Control?.Classification);
        Assert.Null(queuedClaim.OperationId);
        Assert.False(terminalClaim.CanDispatch);
        Assert.Equal(AgentTurnControlClassification.Terminal, terminalClaim.Control?.Classification);
        Assert.Null(terminalClaim.OperationId);
        Assert.False(unknownClaim.CanDispatch);
        Assert.Equal(AgentTurnControlClassification.Terminal, unknownClaim.Control?.Classification);
        Assert.Null(unknownClaim.OperationId);
        Assert.Null(missingClaim.Control);
        Assert.False(missingClaim.CanDispatch);
        Assert.Null(missingClaim.OperationId);
    }

    [Fact]
    public async Task ClaimTurnStop_ConcurrentSameTurnCallsShareOperation()
    {
        var session = await CreateExecutingSessionAsync("project-concurrency", "turn-concurrency");

        var firstTask = session.ClaimTurnStopAsync("turn-concurrency", "operation-a");
        var secondTask = session.ClaimTurnStopAsync("turn-concurrency", "operation-b");
        var claims = await Task.WhenAll(firstTask, secondTask);

        Assert.True(claims[0].CanDispatch);
        Assert.True(claims[1].CanDispatch);
        Assert.True(claims[0].OperationId is "operation-a" or "operation-b");
        Assert.Equal(claims[0].OperationId, claims[1].OperationId);

        await session.CompleteTurnStopAsync("turn-concurrency", "wrong-operation");
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);

        await session.CompleteTurnStopAsync("turn-concurrency", claims[0].OperationId!);
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task ClaimTurnStop_DifferentSessionsKeepClaimsAndCleanupIndependent()
    {
        const string turnId = "same-turn-id";
        var first = await CreateExecutingSessionAsync("project-first", turnId);
        var second = await CreateExecutingSessionAsync("project-second", turnId);

        var firstClaim = await first.ClaimTurnStopAsync(turnId, "first-operation");
        var secondClaim = await second.ClaimTurnStopAsync(turnId, "second-operation");

        Assert.Equal("first-operation", firstClaim.OperationId);
        Assert.Equal("second-operation", secondClaim.OperationId);
        Assert.NotEqual(firstClaim.OperationId, secondClaim.OperationId);

        await first.CompleteTurnStopAsync(turnId, firstClaim.OperationId!);
        var firstReservation = await first.BeginFollowupAsync();
        Assert.False(firstReservation.StartsIdleTurn);
        await first.AbandonFollowupAsync(firstReservation.OperationId!);
        await Assert.ThrowsAsync<StopOperationInProgressException>(second.BeginFollowupAsync);

        await second.CompleteTurnStopAsync(turnId, secondClaim.OperationId!);
        var secondReservation = await second.BeginFollowupAsync();
        Assert.False(secondReservation.StartsIdleTurn);
        await second.AbandonFollowupAsync(secondReservation.OperationId!);
    }

    [Fact]
    public async Task Stop_DifferentProjectsSessionsAndTurnsKeepDispatchPayloadsAndVerdictsIsolated()
    {
        const string firstProjectId = "project-dispatch-first";
        const string secondProjectId = "project-dispatch-second";
        const string firstTurnId = "turn-dispatch-first";
        const string secondTurnId = "turn-dispatch-second";
        const string firstRunnerId = "runner-dispatch-first";
        const string secondRunnerId = "runner-dispatch-second";

        var first = await CreateExecutingSessionAsync(
            firstProjectId,
            firstTurnId,
            firstRunnerId,
            "pi",
            "/work/dispatch-first");
        var second = await CreateExecutingSessionAsync(
            secondProjectId,
            secondTurnId,
            secondRunnerId,
            "opencode",
            "/work/dispatch-second");

        var connections = new RunnerConnectionTracker();
        connections.Register(firstRunnerId, "connection-dispatch-first");
        connections.Register(secondRunnerId, "connection-dispatch-second");
        var firstHub = new RecordingRunnerHubContext();
        firstHub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));
        var secondHub = new RecordingRunnerHubContext();
        secondHub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("not-cancellable"));

        var firstResult = await StopAsync(firstProjectId, first, firstTurnId, firstHub, connections);
        var secondResult = await StopAsync(secondProjectId, second, secondTurnId, secondHub, connections);

        Assert.Equal(TurnControlResultKind.Stopped, firstResult.Kind);
        Assert.Equal(TurnControlResultKind.NotCancellable, secondResult.Kind);
        Assert.Equal(AgentTurnStatus.Completed, Assert.Single(await first.ListTurnsAsync()).Status);
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await second.ListTurnsAsync()).Status);
        var firstInfo = await first.GetAsync() ?? throw new InvalidOperationException("first session was not persisted");
        var secondInfo = await second.GetAsync() ?? throw new InvalidOperationException("second session was not persisted");

        var firstPayload = StopPayload(firstHub);
        var secondPayload = StopPayload(secondHub);
        Assert.Equal(firstProjectId, firstPayload.GetProperty("target").GetProperty("projectId").GetString());
        Assert.Equal(firstInfo.Id, firstPayload.GetProperty("target").GetProperty("sessionId").GetString());
        Assert.Equal(firstTurnId, firstPayload.GetProperty("turnId").GetString());
        Assert.Equal(secondProjectId, secondPayload.GetProperty("target").GetProperty("projectId").GetString());
        Assert.Equal(secondInfo.Id, secondPayload.GetProperty("target").GetProperty("sessionId").GetString());
        Assert.Equal(secondTurnId, secondPayload.GetProperty("turnId").GetString());

        var firstBinding = firstPayload.GetProperty("target").GetProperty("binding");
        var secondBinding = secondPayload.GetProperty("target").GetProperty("binding");
        Assert.Equal("pi", firstBinding.GetProperty("runtime").GetString());
        Assert.Equal(firstRunnerId, firstBinding.GetProperty("runnerId").GetString());
        Assert.Equal("/work/dispatch-first", firstBinding.GetProperty("workDir").GetString());
        Assert.Equal("opencode", secondBinding.GetProperty("runtime").GetString());
        Assert.Equal(secondRunnerId, secondBinding.GetProperty("runnerId").GetString());
        Assert.Equal("/work/dispatch-second", secondBinding.GetProperty("workDir").GetString());
        Assert.NotEqual(
            firstPayload.GetProperty("operationId").GetString(),
            secondPayload.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task StopClaim_DispatchBeforeFailureClearsClaim()
    {
        var session = await CreateExecutingSessionAsync("project-before-dispatch", "turn-before-dispatch");
        var claim = await session.ClaimTurnStopAsync("turn-before-dispatch", "before-dispatch-operation");

        Assert.True(claim.CanDispatch);
        await session.AbandonUndispatchedTurnStopAsync("turn-before-dispatch", claim.OperationId!);

        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task StopClaim_DispatchAfterFailureRetainsClaimUntilMatchingCompletion()
    {
        var session = await CreateExecutingSessionAsync("project-after-dispatch", "turn-after-dispatch");
        var claim = await session.ClaimTurnStopAsync("turn-after-dispatch", "after-dispatch-operation");

        Assert.True(claim.CanDispatch);
        await session.MarkTurnStopDispatchedAsync("turn-after-dispatch", claim.OperationId!);
        await session.AbandonUndispatchedTurnStopAsync("turn-after-dispatch", "wrong-operation");
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);

        await session.CompleteTurnStopAsync("turn-after-dispatch", claim.OperationId!);
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    private async Task<IAgentSessionGrain> CreateSessionAsync(
        string projectId,
        string turnId,
        string runnerId = "runner-stop-grain",
        string runtime = "opencode",
        string workDir = "/work")
    {
        var sessionId = $"generic-stop-grain-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            runtime,
            WorkDir: workDir,
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
                .WithLabel(GenericAgentSessionMetadata.AgentId, "stop-agent")));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            $"runtime-{sessionId}",
            WorkDir: workDir));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{turnId}-{sessionId}",
            turnId,
            "stop turn",
            "generic-followup"));
        return session;
    }

    private async Task<IAgentSessionGrain> CreateExecutingSessionAsync(
        string projectId,
        string turnId,
        string runnerId = "runner-stop-grain",
        string runtime = "opencode",
        string workDir = "/work")
    {
        var session = await CreateSessionAsync(projectId, turnId, runnerId, runtime, workDir);
        await session.MarkTurnExecutingAsync(turnId);
        return session;
    }

    private async Task<TurnControlResult> StopAsync(
        string projectId,
        IAgentSessionGrain session,
        string turnId,
        RecordingRunnerHubContext hub,
        RunnerConnectionTracker connections)
    {
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
            hub,
            connections,
            target,
            turnId,
            CancellationToken.None);
    }

    private static JsonElement StopPayload(RecordingRunnerHubContext hub) =>
        JsonSerializer.SerializeToElement(Assert.Single(hub.Invocations).Arguments.Single());
}
