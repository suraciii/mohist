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
                $$"""{"text":"same text","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}","turnId":"{{first.TurnId}}"}""") },
            "runtime-distinct-keys-executing",
            SessionTurnId: first.TurnId));
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
                $$"""{"text":"identity only","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}","turnId":"{{first.TurnId}}"}""") },
            "runtime-identity-executing",
            SessionTurnId: first.TurnId));

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
                $$"""{"activity":"idle","operationId":"{{first.OperationId}}","status":"completed","turnId":"{{first.TurnId}}"}""") },
            "runtime-identity-terminal",
            SessionTurnId: first.TurnId));

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

}
