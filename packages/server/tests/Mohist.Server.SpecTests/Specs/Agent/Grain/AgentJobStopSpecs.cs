using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Sessions;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("IntegrationSessions")]
public sealed class AgentJobStopSpecs : GenericAgentSessionStopTestSupport
{
    public AgentJobStopSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Stop_UnknownLaunchDelegatesUnknownVerdictToAgentJobOwner()
    {
        var (project, sessionId, turnId, jobId, agentId) =
            await CreateExecutingLaunchSessionForStopAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await AssertLaunchOwnerStateAsync(project.Id, agentId, _runnerId, jobId, expectActive: true);
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("unknown", true));

        var result = await StopAsync(project.Id, sessionId, turnId, hub);

        Assert.Equal(TurnControlResultKind.Unknown, result.Kind);
        Assert.Equal(
            AgentJobStatus.Unknown,
            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(await session.ListTurnsAsync()).Status);
        Assert.Equal("unknown", (await session.GetAsync())!.Status);

        var operationId = StopOperationId(hub);
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);
        await session.CompleteTurnStopAsync(turnId, operationId);
        await Assert.ThrowsAsync<SessionActivityUnknownException>(session.BeginFollowupAsync);
        await AssertLaunchOwnerStateAsync(
            project.Id,
            agentId,
            _runnerId,
            jobId,
            expectActive: true,
            expectRunnerWorkActive: false);

        await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId)
            .FailAsync("correlated-owner-terminal-fact", agentId);
        Assert.Equal(
            AgentJobStatus.Failed,
            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
        await AssertLaunchOwnerStateAsync(project.Id, agentId, _runnerId, jobId, expectActive: false);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_StoppedLaunchLeavesTerminalVerdictToAgentJobOwner()
    {
        var (project, sessionId, turnId, jobId, agentId) =
            await CreateExecutingLaunchSessionForStopAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await AssertLaunchOwnerStateAsync(project.Id, agentId, _runnerId, jobId, expectActive: true);
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var result = await StopAsync(project.Id, sessionId, turnId, hub);

        Assert.Equal(TurnControlResultKind.Stopped, result.Kind);
        Assert.Equal(
            AgentJobStatus.Running,
            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await session.ListTurnsAsync()).Status);

        Assert.False(string.IsNullOrWhiteSpace(StopOperationId(hub)));
        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
        await AssertLaunchOwnerStateAsync(project.Id, agentId, _runnerId, jobId, expectActive: true);

        await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId)
            .FailAsync("owner-terminal-verdict", agentId);
        var turn = Assert.Single(await session.ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Failed, turn.Status);
        Assert.Equal("owner-terminal-verdict", turn.Result?.FailureReason);
        await AssertLaunchOwnerStateAsync(project.Id, agentId, _runnerId, jobId, expectActive: false);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_LaterTurnDoesNotChangeTerminalLaunchOwnerOrJob()
    {
        var (project, sessionId, initialTurnId, jobId, agentId) =
            await CreateExecutingLaunchSessionForStopAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.FailAsync("terminal-before-followup", agentId);
        await session.MarkTurnTerminalAsync(initialTurnId, AgentTurnStatus.Failed, null);

        var laterTurnId = $"turn-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}",
            laterTurnId,
            "later follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(laterTurnId);

        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var result = await StopAsync(project.Id, sessionId, laterTurnId, hub);

        Assert.Equal(TurnControlResultKind.Stopped, result.Kind);
        Assert.Equal(AgentJobStatus.Failed, await job.GetStatusAsync());
        var turns = await session.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Failed, turns.Single(turn => turn.Id == initialTurnId).Status);
        Assert.Equal(AgentTurnStatus.Completed, turns.Single(turn => turn.Id == laterTurnId).Status);
        await AssertTrackedLaunchResourcesReleasedAsync();
        await AssertLaunchOwnerStateAsync(project.Id, agentId, _runnerId, jobId, expectActive: false);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    private async Task<TurnControlResult> StopAsync(
        string projectId,
        string sessionId,
        string turnId,
        RecordingRunnerHubContext hub)
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
            hub,
            RunnerConnections,
            target,
            turnId,
            CancellationToken.None);
    }

    private RecordingRunnerHubContext Hub() =>
        _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
        ?? throw new InvalidOperationException("Recording runner hub context was not registered.");

    private static string StopOperationId(RecordingRunnerHubContext hub) =>
        JsonSerializer.SerializeToElement(Assert.Single(hub.Invocations).Arguments.Single())
            .GetProperty("operationId").GetString()
        ?? throw new InvalidOperationException("stop invocation did not contain an operation id");
}
