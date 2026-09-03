using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Tests.Sessions;

[Collection("AgentSessionGrainComponent")]
[Trait("level", "L0")]
public sealed partial class AgentSessionFollowupGrainSpecs
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
        var provenance = new AgentSessionInputProvenance(
            ProviderKind: "slack",
            WorkspaceId: "T123",
            ConversationId: "C123",
            ThreadId: "1710000000.000001",
            MemberId: "U123",
            MessageId: "1710000000.000002",
            ConnectionId: "connection-1");

        var result = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "follow up text",
            Source: "agent-session-followup",
            IdempotencyKey: "followup-1",
            Provenance: provenance));

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
        Assert.Equal(provenance, input.Provenance);
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
    public async Task AcceptFollowup_QueuedTurn_DoesNotJoinDifferentExecutionSource()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-source-separated-queued");
        var initialProvenance = new AgentSessionInputProvenance(
            ProviderKind: "slack",
            WorkspaceId: "T123",
            ConversationId: "C123",
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

        var slack = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "Slack follow-up waiting for dispatch",
            Source: "agent-session-followup",
            IdempotencyKey: "source-separated-slack",
            Provenance: initialProvenance with { MessageId = "slack-followup" }));
        var nonSlack = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "Web follow-up",
            Source: "agent-session-followup",
            IdempotencyKey: "source-separated-non-slack"));

        Assert.NotEqual(slack.TurnId, nonSlack.TurnId);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Equal([slack.InputId], state!.Status.Turns![1].InputIds);
        Assert.Equal([nonSlack.InputId], state.Status.Turns[2].InputIds);

        var slackDispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(slackDispatch);
        Assert.Equal(slack.InputId, slackDispatch!.InputId);
        Assert.Equal(AgentExecutionSources.Slack, slackDispatch.ExecutionSource);

        await grain.MarkFollowupTurnExecutingAsync(slack.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(slack.OperationId, AgentTurnStatus.Completed, null);

        var nonSlackDispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(nonSlackDispatch);
        Assert.Equal(nonSlack.InputId, nonSlackDispatch!.InputId);
        Assert.Equal(AgentExecutionSources.NonSlack, nonSlackDispatch.ExecutionSource);
        Assert.Null(nonSlackDispatch.Provenance);
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
                $$"""{"text":"first","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}","turnId":"{{first.TurnId}}"}""") },
            "runtime-executing-new-turn",
            SessionTurnId: first.TurnId));

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
                $$"""{"text":"first","kind":"followup","source":"agent-session-followup","operationId":"{{first.OperationId}}","turnId":"{{first.TurnId}}"}""") },
            "runtime-no-interrupt",
            SessionTurnId: first.TurnId));

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
