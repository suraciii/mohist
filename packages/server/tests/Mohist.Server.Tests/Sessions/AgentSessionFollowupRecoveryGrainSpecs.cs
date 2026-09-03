using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Tests.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
    [Fact]
    public async Task AcceptFollowup_ConcurrentAcceptsDuringQueued_JoinSameTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-concurrent-queued");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "concurrent a",
            Source: "agent-session-followup",
            IdempotencyKey: "conq-key-1"));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "concurrent b",
            Source: "agent-session-followup",
            IdempotencyKey: "conq-key-2"));

        Assert.Equal(first.TurnId, second.TurnId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var turn = Assert.Single(state!.Status.Turns!);
        Assert.Equal(2, turn.InputIds.Count);
    }

    [Fact]
    public async Task AcceptFollowup_ConcurrentAcceptsDuringExecuting_CreateSeparateTurns()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-concurrent-executing");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first exec",
            Source: "agent-session-followup",
            IdempotencyKey: "cone-key-1"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"first exec","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}","turnId":"{{first.TurnId}}"}""") },
            "runtime-concurrent-executing",
            SessionTurnId: first.TurnId));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second exec",
            Source: "agent-session-followup",
            IdempotencyKey: "cone-key-2"));
        var third = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "third exec",
            Source: "agent-session-followup",
            IdempotencyKey: "cone-key-3"));

        Assert.NotEqual(first.TurnId, second.TurnId);
        Assert.Equal(second.TurnId, third.TurnId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Turns!.Count);

        var execTurn = state.Status.Turns[0];
        Assert.Equal(AgentTurnStatus.Executing, execTurn.Status);
        Assert.Single(execTurn.InputIds);

        var queuedTurn = state.Status.Turns[1];
        Assert.Equal(AgentTurnStatus.Queued, queuedTurn.Status);
        Assert.Equal(2, queuedTurn.InputIds.Count);
        Assert.Contains(second.InputId, queuedTurn.InputIds);
        Assert.Contains(third.InputId, queuedTurn.InputIds);
    }

    [Fact]
    public async Task AcceptFollowup_EmptyTextWithoutAttachments_Throws()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-empty-text");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "",
                Source: "agent-session-followup",
                IdempotencyKey: "empty-key")));
        Assert.Contains("text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptFollowup_EmptySource_Throws()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-empty-source");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "valid text",
                Source: "",
                IdempotencyKey: "source-key")));
        Assert.Contains("Source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptFollowup_RecoveryInProgress_Throws()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-recovery-block");

        var reserve = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "recovery-key");

        await Assert.ThrowsAsync<RecoveryOperationInProgressException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "during recovery",
                Source: "agent-session-followup",
                IdempotencyKey: "recovery-block-key")));
    }

    [Fact]
    public async Task AcceptFollowup_CompletedRecovery_DoesNotBlock()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-recovery-passed");

        var reserve = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "recovery-done-key");
        await grain.AdmitSessionCommandEffectAsync(reserve.OperationId, "test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(reserve.OperationId, "test-generation", Summary: "done"));

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "after recovery",
            Source: "agent-session-followup",
            IdempotencyKey: "after-recovery-key"));

        Assert.False(result.AlreadyAccepted);
        Assert.NotNull(result.InputId);
        Assert.NotNull(result.TurnId);
    }

    [Fact]
    public async Task InitialTurnTerminal_DispatchesQueuedFollowup()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-initial-terminal-dispatch");
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "initial-input",
            TurnId: "initial-turn",
            Prompt: "initial prompt",
            Source: "agent-launch",
            JobId: "initial-job"));
        await grain.MarkInitialTurnExecutingAsync("initial-job");
        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "queued after launch",
            Source: "agent-session-followup",
            IdempotencyKey: "after-launch"));
        await grain.MarkInitialTurnTerminalAsync("initial-job", AgentTurnStatus.Completed, null);

        var request = Assert.Single(_fixture.FollowupDispatch.Requests);
        Assert.Equal("project-1", request.ProjectId);
        Assert.Equal(sessionId, request.SessionId);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(AgentTurnStatus.Completed, state!.Status.Turns![0].Status);
        Assert.Equal(AgentTurnStatus.Queued, state.Status.Turns[1].Status);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotencyKeyMismatch_Throws()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-idem-mismatch");

        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "original text",
            Source: "agent-session-followup",
            IdempotencyKey: "mismatch-key"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "different text",
                Source: "agent-session-followup",
                IdempotencyKey: "mismatch-key")));
        Assert.Contains("different content", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptFollowup_NoRuntimeSession_Throws()
    {
        var sessionId = $"followup-grain-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());

        await Assert.ThrowsAsync<RuntimeSessionMissingException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "no runtime",
                Source: "agent-session-followup",
                IdempotencyKey: "no-rt-key")));
    }

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
