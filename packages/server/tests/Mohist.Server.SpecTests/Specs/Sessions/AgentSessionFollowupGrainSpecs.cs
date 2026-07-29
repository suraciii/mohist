using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionFollowupGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionFollowupGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task AcceptFollowup_PersistsInputWithStableIdSequenceAndNoJobId()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-followup");

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "follow up text",
            Source: "agent-session-followup",
            IdempotencyKey: "followup-1"));

        Assert.False(string.IsNullOrWhiteSpace(result.InputId));
        Assert.False(string.IsNullOrWhiteSpace(result.TurnId));
        Assert.False(result.AlreadyAccepted);
        Assert.True(result.ShouldRedeliver);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var input = Assert.Single(state!.Status.Inputs!.Skip(0));
        Assert.Equal(result.InputId, input.Id);
        Assert.Equal(1, input.Sequence);
        Assert.Equal("follow up text", input.Text);
        Assert.Equal("agent-session-followup", input.Source);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, input.Acceptance);
        Assert.Null(input.JobId);
        Assert.Equal("followup-1", input.IdempotencyKey);
    }

    [Fact]
    public async Task AcceptFollowup_RecordsAcceptedLeaseWithInputAndTurnIds()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-followup-lease");

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "follow up",
            Source: "agent-session-followup",
            IdempotencyKey: "lease-key"));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var lease = Assert.Single(state!.Status.PendingFollowups!);
        Assert.True(lease.Accepted);
        Assert.NotNull(lease.AcceptedAt);
        Assert.Equal(result.InputId, lease.InputId);
        Assert.Equal(result.TurnId, lease.TurnId);
        Assert.False(string.IsNullOrWhiteSpace(lease.OperationId));
    }

    [Fact]
    public async Task AcceptFollowup_WhileIdleWithNoQueuedTurn_CreatesNewTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-idle-new-turn");

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first follow up",
            Source: "agent-session-followup",
            IdempotencyKey: "key-1"));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var turn = Assert.Single(state!.Status.Turns!);
        Assert.Equal(result.TurnId, turn.Id);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Null(turn.JobId);
        var input = Assert.Single(turn.InputIds);
        Assert.Equal(result.InputId, input);
    }

    [Fact]
    public async Task AcceptFollowup_WhileQueuedTurn_JoinsExistingTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-join-queued");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "join-key-1"));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second",
            Source: "agent-session-followup",
            IdempotencyKey: "join-key-2"));

        Assert.Equal(first.TurnId, second.TurnId);
        Assert.NotEqual(first.InputId, second.InputId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var turn = Assert.Single(state!.Status.Turns!);
        Assert.Equal(2, turn.InputIds.Count);
        Assert.Equal(first.InputId, turn.InputIds[0]);
        Assert.Equal(second.InputId, turn.InputIds[1]);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
    }

    [Fact]
    public async Task AcceptFollowup_DuringExecutingTurn_CreatesNewQueuedTurn()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-executing-new-turn");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "exec-key-1"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"first","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}"}""") },
            "runtime-executing-new-turn"));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second during executing",
            Source: "agent-session-followup",
            IdempotencyKey: "exec-key-2"));

        Assert.NotEqual(first.TurnId, second.TurnId);
        Assert.False(second.AlreadyAccepted);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var turns = state!.Status.Turns!;
        Assert.Equal(2, turns.Count);

        var firstTurn = turns[0];
        Assert.Equal(AgentTurnStatus.Executing, firstTurn.Status);
        Assert.Single(firstTurn.InputIds);
        Assert.Equal(first.InputId, firstTurn.InputIds[0]);

        var secondTurn = turns[1];
        Assert.Equal(AgentTurnStatus.Queued, secondTurn.Status);
        Assert.Single(secondTurn.InputIds);
        Assert.Equal(second.InputId, secondTurn.InputIds[0]);
    }

    [Fact]
    public async Task AcceptFollowup_DuringExecutingTurn_DoesNotInterruptOrMerge()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-no-interrupt");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "no-int-key-1"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"first","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}"}""") },
            "runtime-no-interrupt"));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second during executing",
            Source: "agent-session-followup",
            IdempotencyKey: "no-int-key-2"));

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Turns!.Count);

        var firstTurn = state.Status.Turns[0];
        Assert.Equal(AgentTurnStatus.Executing, firstTurn.Status);
        Assert.Single(firstTurn.InputIds);
        Assert.Equal(first.InputId, firstTurn.InputIds[0]);

        var secondTurn = state.Status.Turns[1];
        Assert.Equal(AgentTurnStatus.Queued, secondTurn.Status);
        Assert.Single(secondTurn.InputIds);
        Assert.Equal(second.InputId, secondTurn.InputIds[0]);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotentRetry_SameKeyReturnsSameInput()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-idempotent");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "retry text",
            Source: "agent-session-followup",
            IdempotencyKey: "retry-key"));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "retry text",
            Source: "agent-session-followup",
            IdempotencyKey: "retry-key"));

        Assert.Equal(first.InputId, second.InputId);
        Assert.Equal(first.TurnId, second.TurnId);
        Assert.False(first.AlreadyAccepted);
        Assert.True(second.AlreadyAccepted);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        var inputs = state!.Status.Inputs!;
        Assert.Single(inputs);
        Assert.Equal(first.InputId, inputs[0].Id);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotentRetry_DifferentKeyCreatesDistinctInput()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-distinct-keys");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "same text",
            Source: "agent-session-followup",
            IdempotencyKey: "distinct-key-1"));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "same text",
            Source: "agent-session-followup",
            IdempotencyKey: "distinct-key-2"));

        // Distinct keys produce distinct inputs. By the turn-assignment
        // rule, the second input joins the same queued turn (no turn
        // execution has begun yet), so both inputs share the turn.
        Assert.NotEqual(first.InputId, second.InputId);
        Assert.Equal(first.TurnId, second.TurnId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Inputs!.Count);
        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(2, turn.InputIds.Count);
    }

    [Fact]
    public async Task AcceptFollowup_DifferentKey_DuringExecuting_CreatesDistinctTurns()
    {
        // Mirrors the join rule test but on the executing branch: when
        // a session.input has already moved the queued turn to
        // Executing, the next distinct-key input starts a new turn.
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-distinct-keys-executing");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "same text",
            Source: "agent-session-followup",
            IdempotencyKey: "distinct-key-1"));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"same text","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}"}""") },
            "runtime-distinct-keys-executing"));
        await persistence.WaitAsync();

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "same text",
            Source: "agent-session-followup",
            IdempotencyKey: "distinct-key-2"));

        Assert.NotEqual(first.InputId, second.InputId);
        Assert.NotEqual(first.TurnId, second.TurnId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Inputs!.Count);
        Assert.Equal(2, state.Status.Turns!.Count);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotentRetry_QueuedTurn_ReAttemptsDelivery()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-redeliver-queued");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "redeliver me",
            Source: "agent-session-followup",
            IdempotencyKey: "redeliver-key"));

        Assert.True(first.ShouldRedeliver);

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "redeliver me",
            Source: "agent-session-followup",
            IdempotencyKey: "redeliver-key"));

        Assert.True(second.AlreadyAccepted);
        Assert.True(second.ShouldRedeliver);
        Assert.Equal(first.InputId, second.InputId);
        Assert.Equal(first.TurnId, second.TurnId);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotentRetry_ExecutingTurn_IdentityOnly()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-identity-executing");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "identity only",
            Source: "agent-session-followup",
            IdempotencyKey: "exec-identity-key"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionInput,
                $$"""{"text":"identity only","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}"}""") },
            "runtime-identity-executing"));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "identity only",
            Source: "agent-session-followup",
            IdempotencyKey: "exec-identity-key"));

        Assert.True(second.AlreadyAccepted);
        Assert.False(second.ShouldRedeliver);
        Assert.Equal(first.InputId, second.InputId);
        Assert.Equal(first.TurnId, second.TurnId);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotentRetry_UsesOriginalOperationId()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-original-operation");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "original-operation-key"));
        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second",
            Source: "agent-session-followup",
            IdempotencyKey: "second-operation-key"));

        var retry = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "original-operation-key"));

        Assert.Equal(first.OperationId, retry.OperationId);
        Assert.Equal(first.InputId, retry.InputId);
        Assert.Equal(first.TurnId, retry.TurnId);
    }

    [Fact]
    public async Task AcceptFollowup_QueuedLeaseSurvivesBeyondDeliveryWindow()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-long-queued");

        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "keep correlation",
            Source: "agent-session-followup",
            IdempotencyKey: "long-queued-key"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var retry = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "keep correlation",
            Source: "agent-session-followup",
            IdempotencyKey: "long-queued-key"));

        Assert.Equal(accepted.OperationId, retry.OperationId);
        Assert.True(retry.ShouldRedeliver);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Single(state!.Status.PendingFollowups!);
    }

    [Fact]
    public async Task AcceptFollowup_IdempotentRetry_TerminalTurn_IdentityOnly()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-identity-terminal");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "terminal identity",
            Source: "agent-session-followup",
            IdempotencyKey: "terminal-identity-key"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{first.OperationId}}","status":"completed"}""") },
            "runtime-identity-terminal"));

        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "terminal identity",
            Source: "agent-session-followup",
            IdempotencyKey: "terminal-identity-key"));

        Assert.True(second.AlreadyAccepted);
        Assert.False(second.ShouldRedeliver);
        Assert.Equal(first.InputId, second.InputId);
        Assert.Equal(first.TurnId, second.TurnId);
    }

    [Fact]
    public async Task AcceptFollowup_OmittedKey_NotRetryIdempotent()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-omitted-key");

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "no key",
            Source: "agent-session-followup",
            IdempotencyKey: string.Empty));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "no key second",
            Source: "agent-session-followup",
            IdempotencyKey: string.Empty));

        Assert.NotEqual(first.InputId, second.InputId);

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Status.Inputs!.Count);
    }

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
                $$"""{"text":"progress me","kind":"followup","source":"agent-session-followup","operationId":"{{accept.OperationId}}"}""") },
            "runtime-progress-executing"));
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
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{accept.OperationId}}","status":"completed"}""") },
            "runtime-progress-terminal"));
        await persistence.WaitAsync();

        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Empty(state!.Status.PendingFollowups!);

        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Completed, turn.Status);
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
                $$"""{"activity":"idle","operationId":"{{accept.OperationId}}","status":"completed"}""") },
            "runtime-clear-lease"));
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
                $$"""{"activity":"idle","operationId":"{{first.OperationId}}","status":"completed"}""") },
            "runtime-after-terminal"));
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
                $$"""{"text":"first exec","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}"}""") },
            "runtime-concurrent-executing"));

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
    public async Task AcceptFollowup_EmptyText_Throws()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-empty-text");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "",
                Source: "agent-session-followup",
                IdempotencyKey: "empty-key")));
        Assert.Contains("Text", ex.Message, StringComparison.Ordinal);
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

        var reserve = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "recovery-key");

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

        var reserve = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "recovery-done-key");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(reserve.OperationId, Summary: "done"));

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

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateAttachedSessionAsync(string runtimeSessionId)
    {
        var sessionId = $"followup-grain-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        return (grain, sessionId);
    }

    private static OpenAgentSessionCommand OpenCommand() => new(
        "runner-1",
        "opencode",
        WorkDir: "/work",
        Metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"));
}
