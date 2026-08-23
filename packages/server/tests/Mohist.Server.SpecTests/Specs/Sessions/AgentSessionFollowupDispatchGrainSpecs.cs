using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
    [Fact]
    public async Task AcceptFollowup_SessionInputEvent_WithOperationId_MarksTurnExecuting()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-input-event-exec");

        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "event exec test",
            Source: "agent-session-followup",
            IdempotencyKey: "event-exec-key"));

        var stateBefore = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(stateBefore);
        Assert.Equal(AgentTurnStatus.Queued, stateBefore!.Status.Turns![0].Status);

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"event exec test","kind":"followup","source":"agent-session-followup","operationId":"{{accept.OperationId}}","turnId":"{{accept.TurnId}}"}""") },
            "runtime-input-event-exec",
            SessionTurnId: accept.TurnId));
        await persistence.WaitAsync();

        var stateAfter = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(stateAfter);
        Assert.Equal(AgentTurnStatus.Executing, stateAfter!.Status.Turns![0].Status);
    }

    [Fact]
    public async Task BeginNextFollowupDispatch_StaysQueuedUntilRuntimeInput()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-claim-stays-queued");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "wait for runtime input",
            Source: "agent-session-followup",
            IdempotencyKey: "claim-stays-queued"));

        var dispatch = await grain.BeginNextFollowupDispatchAsync();

        Assert.NotNull(dispatch);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(AgentTurnStatus.Queued, Assert.Single(state!.Status.Turns!).Status);
        Assert.True(Assert.Single(state.Status.PendingFollowups!).Dispatching);
        Assert.Equal(accepted.OperationId, dispatch!.OperationId);
    }

    [Fact]
    public async Task BeginNextFollowupDispatch_UsesDurableDmRootAndFirstRepresentativeForBatchedSlackInputs()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-slack-batched-root");
        var initialProvenance = new AgentSessionInputProvenance(
            ProviderKind: "slack",
            WorkspaceId: "T123",
            ConversationId: "D123",
            ThreadId: null,
            MemberId: "U123",
            MessageId: "initial-message",
            ConnectionId: "connection-1",
            BoundThreadRootMessageId: "initial-message");
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "initial-input",
            TurnId: "initial-turn",
            Prompt: "initial prompt",
            Source: "agent-connection",
            JobId: "initial-job",
            Provenance: initialProvenance));
        await grain.MarkInitialTurnTerminalAsync("initial-job", AgentTurnStatus.Completed, null);

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first queued input",
            Source: "agent-session-followup",
            IdempotencyKey: "slack-batched-first",
            Provenance: initialProvenance with { MessageId = "first-message" }));
        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second queued input",
            Source: "agent-session-followup",
            IdempotencyKey: "slack-batched-second",
            Provenance: initialProvenance with { MessageId = "second-message" }));

        var dispatch = await grain.BeginNextFollowupDispatchAsync();

        Assert.NotNull(dispatch);
        Assert.Equal(AgentExecutionSources.Slack, dispatch!.ExecutionSource);
        Assert.Equal(first.InputId, dispatch.InputId);
        Assert.Equal(["first queued input", "second queued input"], dispatch.InputTexts);
        Assert.NotNull(dispatch.Provenance);
        Assert.Equal("initial-message", dispatch.Provenance!.BoundThreadRootMessageId);
        Assert.Equal("first-message", dispatch.Provenance.MessageId);
    }

    [Fact]
    public async Task BeginFollowupDispatchForTurn_TargetsRequestedTurnAndLeavesOtherQueued()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("targeted-dispatch");
        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first queued input",
            Source: "agent-session-followup",
            IdempotencyKey: "targeted-first"));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second queued input",
            Source: "agent-session-followup",
            IdempotencyKey: "targeted-second",
            ForceNewTurn: true));

        var dispatch = await grain.BeginFollowupDispatchForTurnAsync(second.TurnId);

        Assert.NotNull(dispatch);
        Assert.Equal(second.TurnId, dispatch!.TurnId);
        Assert.Equal(second.InputId, dispatch.InputId);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(AgentTurnStatus.Queued, state!.Status.Turns!.Single(turn => turn.Id == first.TurnId).Status);
        Assert.NotNull(state.Status.PendingFollowups);
        var pending = state.Status.PendingFollowups!;
        Assert.False(pending.Single(lease => lease.TurnId == first.TurnId).Dispatching);
        Assert.True(pending.Single(lease => lease.TurnId == second.TurnId).Dispatching);
    }

    [Fact]
    public async Task BeginFollowupDispatchForTurn_RespectsLaunchTurnGuard()
    {
        var (grain, _) = await CreateAttachedSessionAsync("targeted-dispatch-launch-guard");
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "launch-input",
            TurnId: "launch-turn",
            Prompt: "launch",
            Source: "agent-connection",
            JobId: "launch-job"));

        Assert.Null(await grain.BeginFollowupDispatchForTurnAsync("launch-turn"));
    }

    [Fact]
    public async Task BeginFollowupDispatchForTurn_BusySessionLeavesRetryQueuedForOrdinaryScheduler()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("targeted-dispatch-busy");
        var executing = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "currently executing",
            Source: "agent-session-followup",
            IdempotencyKey: "targeted-executing"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"currently executing","kind":"followup","source":"agent-session-followup","operationId":"{{executing.OperationId}}"}""") },
            "targeted-dispatch-busy"));

        var retry = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "retry while busy",
            Source: "agent-session-followup",
            IdempotencyKey: "targeted-retry-busy",
            ForceNewTurn: true));

        Assert.Null(await grain.BeginFollowupDispatchForTurnAsync(retry.TurnId));
        var before = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(before);
        Assert.Equal(AgentTurnStatus.Executing, before!.Status.Turns!.Single(turn => turn.Id == executing.TurnId).Status);
        Assert.Equal(AgentTurnStatus.Queued, before.Status.Turns!.Single(turn => turn.Id == retry.TurnId).Status);
        Assert.False(before.Status.PendingFollowups!.Single(lease => lease.TurnId == retry.TurnId).Dispatching);

        await grain.MarkFollowupTurnTerminalAsync(executing.OperationId, AgentTurnStatus.Completed, null);
        var ordinary = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(ordinary);
        Assert.Equal(retry.TurnId, ordinary!.TurnId);
    }

    [Fact]
    public async Task AcceptFollowup_ForceNewTurn_UsesPreMintedTurnIdentity()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("targeted-pre-mint");
        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "targeted-pre-mint-first"));

        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "retry",
            Source: "agent-session-followup",
            IdempotencyKey: "targeted-pre-mint-retry",
            PreMintedInputId: "retry-input",
            PreMintedTurnId: "retry-turn",
            ForceNewTurn: true));

        Assert.Equal("retry-input", accepted.InputId);
        Assert.Equal("retry-turn", accepted.TurnId);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Contains(state!.Status.Turns!, turn => turn.Id == "retry-turn");
    }

    [Fact]
    public async Task AcceptFollowup_DispatchInProgress_CreatesNextQueuedTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-join-dispatching-turn");
        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first queued input",
            Source: "agent-session-followup",
            IdempotencyKey: "join-dispatching-first"));
        await grain.BeginNextFollowupDispatchAsync();

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second queued input",
            Source: "agent-session-followup",
            IdempotencyKey: "join-dispatching-second"));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Turns!.Count);
        Assert.NotEqual(first.TurnId, second.TurnId);
        Assert.Equal([first.InputId], state.Status.Turns[0].InputIds);
        Assert.Equal([second.InputId], state.Status.Turns[1].InputIds);
        Assert.True(state.Status.PendingFollowups!.Single(lease => lease.TurnId == first.TurnId).Dispatching);
    }

    [Fact]
    public async Task ReleaseFollowupDispatch_KeepsQueuedPayloadSealed()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-sealed-after-release");
        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "sealed first input",
            Source: "agent-session-followup",
            IdempotencyKey: "sealed-first"));
        await grain.BeginNextFollowupDispatchAsync();
        await grain.ReleaseFollowupDispatchAsync(first.OperationId);

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "sealed second input",
            Source: "agent-session-followup",
            IdempotencyKey: "sealed-second"));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Turns!.Count);
        Assert.NotEqual(first.TurnId, second.TurnId);
        var firstLease = state.Status.PendingFollowups!.Single(lease => lease.TurnId == first.TurnId);
        Assert.False(firstLease.Dispatching);
        Assert.True(firstLease.PayloadSealed);
    }

    [Fact]
    public async Task Activate_ReclaimsQueuedDispatchForSameKeyRetry()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-reclaim-dispatch");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "reclaim delivery",
            Source: "agent-session-followup",
            IdempotencyKey: "reclaim-dispatch"));
        await grain.BeginNextFollowupDispatchAsync();

        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await reactivated.GetAsync();

        var retry = await reactivated.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "reclaim delivery",
            Source: "agent-session-followup",
            IdempotencyKey: "reclaim-dispatch"));
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(AgentTurnStatus.Queued, Assert.Single(state!.Status.Turns!).Status);
        Assert.False(Assert.Single(state.Status.PendingFollowups!).Dispatching);
        Assert.True(Assert.Single(state.Status.PendingFollowups!).PayloadSealed);
        Assert.True(retry.ShouldRedeliver);
        Assert.Equal(accepted.InputId, retry.InputId);
    }
}
