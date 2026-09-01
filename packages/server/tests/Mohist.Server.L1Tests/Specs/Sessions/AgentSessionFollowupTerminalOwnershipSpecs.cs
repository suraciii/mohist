using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
    [Fact]
    public async Task InitialRuntimeIdle_DoesNotCompleteQueuedFollowupBeforeDispatch()
    {
        const string runtimeSessionId = "runtime-initial-idle-before-followup";
        var (grain, sessionId) = await CreateAttachedSessionAsync(runtimeSessionId);
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "initial-idle-input",
            TurnId: "initial-idle-turn",
            Prompt: "initial prompt",
            Source: "agent-launch",
            JobId: "initial-idle-job"));
        await grain.MarkInitialTurnExecutingAsync("initial-idle-job");
        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "continue after the initial run",
            Source: "agent-session-followup",
            IdempotencyKey: "initial-idle-followup"));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                """{"activity":"idle","status":"completed"}""") },
            runtimeSessionId));
        await persistence.WaitAsync();

        var beforeTerminal = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(beforeTerminal);
        Assert.Equal(
            AgentTurnStatus.Queued,
            beforeTerminal!.Status.Turns!.Single(turn => turn.Id == followup.TurnId).Status);
        Assert.Contains(
            beforeTerminal.Status.PendingFollowups!,
            lease => lease.OperationId == followup.OperationId);

        await grain.MarkInitialTurnTerminalAsync("initial-idle-job", AgentTurnStatus.Completed, null);

        var request = Assert.Single(_fixture.FollowupDispatch.Requests);
        Assert.Equal("project-1", request.ProjectId);
        Assert.Equal(sessionId, request.SessionId);
        var afterTerminal = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(afterTerminal);
        Assert.Equal(
            AgentTurnStatus.Queued,
            afterTerminal!.Status.Turns!.Single(turn => turn.Id == followup.TurnId).Status);
    }

    [Fact]
    public async Task ReplayedInitialTerminalClose_DoesNotCompleteExecutingFollowup()
    {
        const string runtimeSessionId = "runtime-late-initial-terminal";
        const string initialJobId = "late-initial-job";
        var (grain, sessionId) = await CreateAttachedSessionAsync(runtimeSessionId);
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "late-initial-input",
            TurnId: "late-initial-turn",
            Prompt: "initial prompt",
            Source: "agent-launch",
            JobId: initialJobId));
        await grain.MarkInitialTurnTerminalAsync(initialJobId, AgentTurnStatus.Completed, null);
        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "continue while an old terminal close replays",
            Source: "agent-session-followup",
            IdempotencyKey: "late-initial-followup"));
        await grain.MarkTurnExecutingAsync(followup.TurnId);

        await grain.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            SessionId: sessionId,
            DeliveryId: "late-initial-terminal-delivery",
            Status: "completed",
            ExitCode: 0,
            FailureReason: null,
            FailureCategory: null,
            RecordedAt: _fixture.TimeProvider.GetUtcNow(),
            PayloadJson: $$"""{"agentJobId":"{{initialJobId}}"}""",
            RuntimeSessionId: runtimeSessionId));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(
            AgentTurnStatus.Executing,
            state!.Status.Turns!.Single(turn => turn.Id == followup.TurnId).Status);
        Assert.Contains(
            state.Status.PendingFollowups!,
            lease => lease.OperationId == followup.OperationId);
    }
}
