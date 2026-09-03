using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
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
