using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.L0Tests.Specs.Sessions;

public sealed partial class AgentSessionFollowupGrainSpecs
{
    [Fact]
    public async Task AcceptFollowup_CapacityExceeded_RejectsNewInput()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-capacity");

        for (var i = 0; i < 16; i++)
        {
            var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: $"follow-up {i}",
                Source: "agent-session-followup",
                IdempotencyKey: $"capacity-key-{i}"));
            Assert.False(result.AlreadyAccepted);
        }

        var exception = await Assert.ThrowsAsync<AgentSessionFollowupCapacityExceededException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "too many",
                Source: "agent-session-followup",
                IdempotencyKey: "capacity-key-overflow")));

        Assert.Equal(sessionId, exception.SessionId);
        Assert.Equal(16, exception.Capacity);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(16, state!.Status.Inputs!.Count);
    }

    [Fact]
    public async Task MarkFollowupTurnExecuting_RuntimeEvent_ProgressesTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-progress-executing");

        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "progress me",
            Source: "agent-session-followup",
            IdempotencyKey: "progress-exec-key"));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"progress me","kind":"followup","source":"agent-session-followup","operationId":"{{accept.OperationId}}","turnId":"{{accept.TurnId}}"}""") },
            "runtime-progress-executing",
            SessionTurnId: accept.TurnId));
        await persistence.WaitAsync();

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var turn = Assert.Single(state!.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Executing, turn.Status);
    }

    [Fact]
    public async Task MarkFollowupTurnTerminal_RuntimeEvent_ProgressesTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-progress-terminal");

        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "complete me",
            Source: "agent-session-followup",
            IdempotencyKey: "progress-term-key"));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.MessageDelta,
                $$"""{"text":"follow-up assistant output","turnId":"{{accept.TurnId}}"}"""),
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    $$"""{"activity":"idle","operationId":"{{accept.OperationId}}","status":"completed","message":"follow-up assistant output","output":"follow-up assistant output","turnId":"{{accept.TurnId}}"}""") },
            "runtime-progress-terminal",
            SessionTurnId: accept.TurnId));
        await persistence.WaitAsync();

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Empty(state!.Status.PendingFollowups!);

        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Completed, turn.Status);
        Assert.Equal("follow-up assistant output", turn.Result?.Message);
        Assert.Equal("follow-up assistant output", turn.Result?.Output);
    }

    [Fact]
    public async Task MarkFollowupTurnTerminal_RuntimeFailure_PreservesRetryableCategoryAndFailedStatus()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-generation-drain-timeout");

        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "fail with a generation drain timeout",
            Source: "agent-session-followup",
            IdempotencyKey: "generation-drain-timeout-key"));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"unknown","operationId":"{{accept.OperationId}}","status":"failed","failureReason":"generation did not drain","failureCategory":"generation-drain-timeout","turnId":"{{accept.TurnId}}"}""") },
            "runtime-generation-drain-timeout"));
        await persistence.WaitAsync();

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Empty(state!.Status.PendingFollowups!);

        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Failed, turn.Status);
        Assert.Equal("generation did not drain", turn.Result?.FailureReason);
        Assert.Equal("generation-drain-timeout", turn.Result?.FailureCategory);
    }

    [Fact]
    public async Task MarkFollowupTurnTerminal_SessionActivityIdle_ClearsLease()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-clear-lease");

        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "clear lease",
            Source: "agent-session-followup",
            IdempotencyKey: "clear-lease-key"));

        Assert.Single((await _fixture.StateStore.LoadAsync(sessionId))!.Status.PendingFollowups!);

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{accept.OperationId}}","status":"completed","turnId":"{{accept.TurnId}}"}""") },
            "runtime-clear-lease",
            SessionTurnId: accept.TurnId));
        await persistence.WaitAsync();

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Empty(state!.Status.PendingFollowups!);
    }

    [Fact]
    public async Task AcceptFollowup_AfterTerminal_FollowupCreatesNewTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-after-terminal");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first turn",
            Source: "agent-session-followup",
            IdempotencyKey: "after-term-1"));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{first.OperationId}}","status":"completed","turnId":"{{first.TurnId}}"}""") },
            "runtime-after-terminal",
            SessionTurnId: first.TurnId));
        await persistence.WaitAsync();

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second turn after terminal",
            Source: "agent-session-followup",
            IdempotencyKey: "after-term-2"));

        Assert.NotEqual(first.TurnId, second.TurnId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Turns!.Count);
        Assert.Equal(AgentTurnStatus.Completed, state.Status.Turns[0].Status);
        Assert.Equal(AgentTurnStatus.Queued, state.Status.Turns[1].Status);
    }

    [Fact]
    public async Task AcceptFollowup_SurvivesGrainStateReload()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-durability");

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "durable input",
            Source: "agent-session-followup",
            IdempotencyKey: "durability-key"));

        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();

        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await reactivated.GetAsync();

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var input = Assert.Single(state!.Status.Inputs!);
        Assert.Equal(result.InputId, input.Id);
        Assert.Equal("durable input", input.Text);
        Assert.Equal("agent-session-followup", input.Source);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, input.Acceptance);
        Assert.Null(input.JobId);

        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(result.TurnId, turn.Id);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Null(turn.JobId);
        var turnInput = Assert.Single(turn.InputIds);
        Assert.Equal(result.InputId, turnInput);
    }

    [Fact]
    public async Task AcceptFollowup_DoesNotCreateAgentJob()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-no-job");

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "no job",
            Source: "agent-session-followup",
            IdempotencyKey: "no-job-key"));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var input = Assert.Single(state!.Status.Inputs!);
        Assert.Null(input.JobId);

        var turn = Assert.Single(state.Status.Turns!);
        Assert.Null(turn.JobId);
    }

}
