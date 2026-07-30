using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
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
                $$"""{"text":"event exec test","kind":"followup","source":"agent-session-followup","operationId":"{{accept.OperationId}}"}""") },
            "runtime-input-event-exec"));
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
