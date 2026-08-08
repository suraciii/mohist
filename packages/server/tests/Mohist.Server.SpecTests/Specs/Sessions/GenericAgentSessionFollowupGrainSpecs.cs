using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class GenericAgentSessionFollowupGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public GenericAgentSessionFollowupGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task IdleFollowup_StartsQueuedUserTurnWithoutReplacingBinding()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-idle-followup");

        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "start an idle turn",
            Source: "agent-session-followup",
            IdempotencyKey: "idle-followup"));

        Assert.False(accepted.AlreadyAccepted);
        Assert.False(string.IsNullOrWhiteSpace(accepted.InputId));
        Assert.False(string.IsNullOrWhiteSpace(accepted.TurnId));
        var state = await LoadAsync(sessionId);
        Assert.Equal("runtime-idle-followup", state.Status.AgentRuntimeSessionId);
        var input = Assert.Single(state.Status.Inputs!);
        Assert.Equal("start an idle turn", input.Text);
        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Equal(accepted.InputId, Assert.Single(turn.InputIds));
    }

    [Fact]
    public async Task SameIdempotencyKey_ReturnsOriginalInputTurnAndRedeliveryObservation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-same-key");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "same input",
            Source: "agent-session-followup",
            IdempotencyKey: "same-key"));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "same input",
            Source: "agent-session-followup",
            IdempotencyKey: "same-key"));

        Assert.False(first.AlreadyAccepted);
        Assert.True(second.AlreadyAccepted);
        Assert.True(second.ShouldRedeliver);
        Assert.Equal(first.InputId, second.InputId);
        Assert.Equal(first.TurnId, second.TurnId);
        var state = await LoadAsync(sessionId);
        Assert.Single(state.Status.Inputs!);
        Assert.Single(state.Status.Turns!);
    }

    [Fact]
    public async Task ExecutingTurn_AcceptsFollowupAsNextQueuedTurnWithoutInterruptingCurrentTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-executing-followup");
        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "first-key"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"first","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}"}""") },
            "runtime-executing-followup"));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second",
            Source: "agent-session-followup",
            IdempotencyKey: "second-key"));

        Assert.NotEqual(first.TurnId, second.TurnId);
        var state = await LoadAsync(sessionId);
        Assert.Equal(2, state.Status.Turns!.Count);
        Assert.Equal(AgentTurnStatus.Executing, state.Status.Turns[0].Status);
        Assert.Equal(AgentTurnStatus.Queued, state.Status.Turns[1].Status);
    }

    [Fact]
    public async Task ClaimedDelivery_SealsCurrentPayloadAndQueuesNextTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-claimed-followup");
        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first input",
            Source: "agent-session-followup",
            IdempotencyKey: "first-claim"));
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(dispatch);

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second input",
            Source: "agent-session-followup",
            IdempotencyKey: "second-claim"));

        Assert.NotEqual(first.TurnId, second.TurnId);
        var state = await LoadAsync(sessionId);
        Assert.Equal(2, state.Status.Turns!.Count);
        var firstLease = state.Status.PendingFollowups!.Single(lease => lease.TurnId == first.TurnId);
        Assert.True(firstLease.Dispatching);
        Assert.Equal(AgentTurnStatus.Queued, state.Status.Turns[1].Status);
    }

    [Fact]
    public async Task ReleasedDeliveryClaim_RetryWithSameKeyRedeliversOriginalTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-release-followup");
        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "retry after cancellation",
            Source: "agent-session-followup",
            IdempotencyKey: "retry-claim"));
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(dispatch);

        await grain.ReleaseFollowupDispatchAsync(dispatch!.OperationId);
        var retry = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "retry after cancellation",
            Source: "agent-session-followup",
            IdempotencyKey: "retry-claim"));

        Assert.True(retry.AlreadyAccepted);
        Assert.True(retry.ShouldRedeliver);
        Assert.Equal(first.InputId, retry.InputId);
        Assert.Equal(first.TurnId, retry.TurnId);
        var state = await LoadAsync(sessionId);
        var lease = Assert.Single(state.Status.PendingFollowups!);
        Assert.False(lease.Dispatching);
        Assert.True(lease.PayloadSealed);
    }

    [Fact]
    public async Task PendingDeliveryBlocksRecoveryUntilMatchingTerminalActivity()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-recovery-block");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "hold recovery",
            Source: "agent-session-followup",
            IdempotencyKey: "recovery-block"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(Summary: "blocked")));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{accepted.OperationId}}","status":"completed"}""") },
            "runtime-recovery-block"));

        var compacted = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));
        Assert.Equal(sessionId, compacted.Id);
        Assert.True(compacted.WasCompacted);
        Assert.Empty((await LoadAsync(sessionId)).Status.PendingFollowups!);
    }

    [Fact]
    public async Task AfterReset_FollowupDispatchUsesReplacementRuntimeBinding()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-reset");
        await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "runtime-before-reset",
            ReplacementRuntimeSessionId: "runtime-replacement"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"idle\",\"status\":\"completed\",\"operationId\":\"old-runtime-terminal\"}") },
            "runtime-before-reset"));
        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "continue on replacement",
            Source: "agent-session-followup",
            IdempotencyKey: "after-reset"));

        var dispatch = await grain.BeginNextFollowupDispatchAsync();

        Assert.NotNull(dispatch);
        var state = await LoadAsync(sessionId);
        var lease = Assert.Single(state.Status.PendingFollowups!);
        Assert.Equal("runtime-replacement", lease.RuntimeSessionId);
        Assert.Equal("opencode", state.Runtime.Runtime);
    }

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateAttachedSessionAsync(string runtimeSessionId)
    {
        var sessionId = $"generic-followup-grain-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, "project-1")
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
                .WithLabel(GenericAgentSessionMetadata.AgentId, "agent-1")
                .WithLabel(GenericAgentSessionMetadata.AgentName, "Agent One")));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId, WorkDir: "/work"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        return (grain, sessionId);
    }

    private async Task<AgentSession> LoadAsync(string sessionId) =>
        await _fixture.StateStore.LoadAsync(sessionId)
        ?? throw new InvalidOperationException($"Missing AgentSession {sessionId}.");
}
