using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Collection("AgentSessionGrainComponent")]
[Trait("level", "L0")]
public sealed class AgentSessionCanonicalRefreshGrainSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionCanonicalRefreshGrainSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptFollowup_PublishesOnlyAfterInputAndQueuedTurnCommit(bool attachRuntime)
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open("opencode"));
        if (attachRuntime)
            await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1"));
        else
            await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
                "initial-input", "initial-turn", "initial prompt", "agent-connection", "initial-job"));

        var publicationCount = Fixture.TranscriptPublisher.Published.Count;
        var entered = Signal();
        var release = Signal();
        Fixture.StateStore.BeforeSaveAsync = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        AgentSession? publishedState = null;
        Fixture.TranscriptPublisher.BeforePublish = envelope =>
        {
            if (IsCanonicalHint(envelope)) publishedState = Fixture.StateStore.State;
        };
        var eventCount = Fixture.StateStore.Events.Count;
        var command = new AcceptFollowupCommand("accepted input", "agent-session-followup", "accepted-key",
            AllowPendingInitialLaunch: !attachRuntime);
        var accepting = grain.AcceptFollowupAsync(command);
        await entered.Task;
        try
        {
            Assert.Empty(CanonicalHintsSince(publicationCount));
            Assert.DoesNotContain(Fixture.StateStore.State!.Status.Inputs ?? [], input => input.Text == command.Text);
        }
        finally { release.TrySetResult(); }
        var accepted = await accepting;

        AssertHint(Assert.Single(CanonicalHintsSince(publicationCount)), grain);
        Assert.NotNull(publishedState);
        var input = Assert.Single(publishedState.Status.Inputs!, input => input.Id == accepted.InputId);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, input.Acceptance);
        var turn = Assert.Single(publishedState.Status.Turns!, turn => turn.Id == accepted.TurnId);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        Assert.Contains(input.Id, turn.InputIds);
        Assert.Equal(attachRuntime ? "runtime-1" : null, publishedState.Status.AgentRuntimeSessionId);
        Assert.Empty(Fixture.TranscriptStore.Flushes);
        Assert.Equal(eventCount, Fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task AcceptFollowup_SaveFailureDoesNotPublishOrPersistInput()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var publicationCount = Fixture.TranscriptPublisher.Published.Count;
        Fixture.StateStore.FailNextSave(grain.GetPrimaryKeyString(), new InvalidOperationException("state unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AcceptFollowupAsync(
            new AcceptFollowupCommand("not committed", "agent-session-followup", "failed-key")));

        Assert.Empty(CanonicalHintsSince(publicationCount));
        Assert.Empty(Fixture.StateStore.State!.Status.Inputs ?? []);
    }

    [Fact]
    public async Task AcceptFollowup_PublishFailurePreservesAcceptanceAndIdempotentReplay()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var publishedBefore = Fixture.TranscriptPublisher.Published.ToArray();
        var attempts = 0;
        Fixture.TranscriptPublisher.BeforePublish = envelope =>
        {
            if (!IsCanonicalHint(envelope)) return;
            attempts++;
            throw new InvalidOperationException("publisher unavailable");
        };
        var command = new AcceptFollowupCommand("committed input", "agent-session-followup", "stable-key");

        var accepted = await grain.AcceptFollowupAsync(command);
        var dispatchCount = Fixture.FollowupDispatch.Requests.Count;
        var replay = await grain.AcceptFollowupAsync(command);

        Assert.False(accepted.AlreadyAccepted);
        Assert.True(replay.AlreadyAccepted);
        Assert.Equal(accepted.InputId, replay.InputId);
        Assert.Equal(accepted.OperationId, replay.OperationId);
        Assert.Equal(1, attempts);
        Assert.Equal(publishedBefore, Fixture.TranscriptPublisher.Published);
        Assert.Equal(dispatchCount, Fixture.FollowupDispatch.Requests.Count);
        Assert.Single(Fixture.StateStore.State!.Status.Inputs!);
        Assert.Single(Fixture.StateStore.State.Status.Turns!);
        var warning = Assert.Single(Fixture.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("transcript publish failed", warning.Message);
    }

    [Theory]
    [InlineData("message.delta", "text")]
    [InlineData("reasoning.delta", "reasoning")]
    public async Task RuntimeBatch_PublishesAfterExecutingStateAndLastChunkPersist(string eventType, string partType)
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "continue", "agent-session-followup", "executing-key"));
        var publicationCount = Fixture.TranscriptPublisher.Published.Count;
        var stateEntered = Signal();
        var stateRelease = Signal();
        var transcriptEntered = Signal();
        var transcriptRelease = Signal();
        Fixture.StateStore.BeforeSaveAsync = async _ =>
        {
            stateEntered.TrySetResult();
            await stateRelease.Task;
        };
        Fixture.TranscriptStore.BeforeSaveAsync = async _ =>
        {
            transcriptEntered.TrySetResult();
            await transcriptRelease.Task;
        };
        AgentTurnStatus? publishedTurnStatus = null;
        string? publishedText = null;
        Fixture.TranscriptPublisher.BeforePublish = envelope =>
        {
            if (!IsCanonicalHint(envelope)) return;
            publishedTurnStatus = Fixture.StateStore.State!.Status.Turns!.Single().Status;
            publishedText = Fixture.TranscriptStore.Flushes.Single().Parts.Single().TextDelta;
        };
        var checkpoint = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
        new AgentSessionRuntimeEventInput[]
        {
            new(RuntimeEventTypes.SessionInput,
                $$"""{"text":"continue","kind":"followup","source":"agent-session-followup","operationId":"{{accepted.OperationId}}","turnId":"{{accepted.TurnId}}"}"""),
            new(eventType, $$"""{"text":"first ","turnId":"{{accepted.TurnId}}"}"""),
        }, "runtime-1", SessionTurnId: accepted.TurnId));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new AgentSessionRuntimeEventInput[] { new(eventType, $$"""{"text":"last chunk","turnId":"{{accepted.TurnId}}"}""") },
            "runtime-1", SessionTurnId: accepted.TurnId));

        var persistence = checkpoint.WaitAsync();
        await stateEntered.Task;
        try
        {
            Assert.Empty(CanonicalHintsSince(publicationCount));
            Assert.Equal(AgentTurnStatus.Queued, Fixture.StateStore.State!.Status.Turns!.Single().Status);
            stateRelease.TrySetResult();
            await transcriptEntered.Task;
            Assert.Empty(CanonicalHintsSince(publicationCount));
            Assert.Equal(AgentTurnStatus.Executing, Fixture.StateStore.State!.Status.Turns!.Single().Status);
            Assert.Empty(Fixture.TranscriptStore.Flushes);
        }
        finally
        {
            stateRelease.TrySetResult();
            transcriptRelease.TrySetResult();
        }
        Assert.Equal(AgentSessionPersistenceOutcome.Succeeded, (await persistence).Outcome);

        AssertHint(Assert.Single(CanonicalHintsSince(publicationCount)), grain);
        Assert.Equal(AgentTurnStatus.Executing, publishedTurnStatus);
        Assert.Equal("first last chunk", publishedText);
        var part = Assert.Single(Assert.Single(Fixture.TranscriptStore.Flushes).Parts);
        Assert.Equal(partType, part.Type);
        Assert.Equal("first last chunk", part.TextDelta);
        Assert.Equal([RuntimeEventTypes.SessionInput, eventType, eventType],
            Fixture.TranscriptPublisher.Published.Skip(publicationCount)
                .Where(envelope => envelope.RuntimeSessionId == "runtime-1")
                .Select(envelope => envelope.Type));

        await DeactivateAsync(grain);
        Assert.Single(CanonicalHintsSince(publicationCount));
        Assert.Single(Fixture.TranscriptStore.Flushes);
    }

    [Fact]
    public async Task TerminalClose_PublishesAfterFinalTextAndIdleStatePersist()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        var publicationCount = Fixture.TranscriptPublisher.Published.Count;
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new AgentSessionRuntimeEventInput[] { new(RuntimeEventTypes.MessageDelta, "{\"text\":\"final answer\"}") }, "runtime-1"));
        var entered = Signal();
        var release = Signal();
        Fixture.TranscriptStore.BeforeSaveAsync = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        AgentSessionActivity? publishedActivity = null;
        string? publishedText = null;
        Fixture.TranscriptPublisher.BeforePublish = envelope =>
        {
            if (!IsCanonicalHint(envelope)) return;
            publishedActivity = Fixture.StateStore.State!.Status.Activity;
            publishedText = Fixture.TranscriptStore.Flushes.Single().Parts
                .Single(part => part.Type == TranscriptPartTypes.Text).TextDelta;
        };
        var closing = grain.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
            grain.GetPrimaryKeyString(), "terminal-delivery", "completed", 0, null, null,
            Fixture.TimeProvider.GetUtcNow(), "{}", "runtime-1"));
        await entered.Task;
        try
        {
            Assert.Empty(CanonicalHintsSince(publicationCount));
            Assert.Empty(Fixture.TranscriptStore.Flushes);
        }
        finally { release.TrySetResult(); }
        await closing;

        AssertHint(Assert.Single(CanonicalHintsSince(publicationCount)), grain);
        Assert.Equal(AgentSessionActivity.Idle, publishedActivity);
        Assert.Equal("final answer", publishedText);
        var flush = Assert.Single(Fixture.TranscriptStore.Flushes);
        Assert.Equal(2, flush.Parts.Count);
        Assert.Single(flush.Parts, part => part.Type == RuntimeEventTypes.SessionActivity);
        Assert.Single(Fixture.TranscriptPublisher.Published.Skip(publicationCount),
            envelope => envelope.Type == RuntimeEventTypes.SessionActivity && envelope.RuntimeSessionId == "runtime-1");
    }

    [Fact]
    public async Task ManagerRecovery_StateOnlyPersistencePublishesOneHint()
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open("opencode") with
        {
            Metadata = GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                SlackDeliveryOwnerIds.ManagerProjectId, "agent-1", "Manager")),
        });
        var publicationCount = Fixture.TranscriptPublisher.Published.Count;
        var checkpoint = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.RecordManagerRecoveryTurnAsync(new RecordFollowupTurnCommand(
            "recovery-input", "recovery-turn", "inspect prior execution", "manager-recovery",
            Provenance: new AgentSessionInputProvenance("slack", "T1", "D1", null, "U1", "message-1",
                ConnectionId: "connection-1", BoundThreadRootMessageId: "message-1")));
        Assert.Empty(CanonicalHintsSince(publicationCount));
        Assert.Empty(Fixture.TranscriptStore.Flushes);

        var entered = Signal();
        var release = Signal();
        Fixture.StateStore.BeforeSaveAsync = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var persistence = checkpoint.WaitAsync();
        await entered.Task;
        try { Assert.Empty(CanonicalHintsSince(publicationCount)); }
        finally { release.TrySetResult(); }
        Assert.Equal(AgentSessionPersistenceOutcome.Succeeded, (await persistence).Outcome);

        AssertHint(Assert.Single(CanonicalHintsSince(publicationCount)), grain);
        Assert.Empty(Fixture.TranscriptStore.Flushes);
        Assert.Equal(AgentTurnStatus.Queued, Assert.Single(Fixture.StateStore.State!.Status.Turns!).Status);
        await DeactivateAsync(grain);
        Assert.Single(CanonicalHintsSince(publicationCount));
    }

    [Fact]
    public async Task Compact_PublishesCanonicalHintAfterRecoveryEvidenceCommit()
    {
        var grain = await OpenBoundGrainAsync("opencode");
        Fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var publicationCount = Fixture.TranscriptPublisher.Published.Count;
        var entered = Signal();
        var release = Signal();
        Fixture.TranscriptStore.BeforeSaveAsync = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        AgentSession? publishedState = null;
        Fixture.TranscriptPublisher.BeforePublish = envelope =>
        {
            if (IsCanonicalHint(envelope)) publishedState = Fixture.StateStore.State;
        };
        var compacting = grain.CompactAsync(new CompactAgentSessionCommand(Summary: "saved context"));
        await entered.Task;
        try
        {
            Assert.Empty(CanonicalHintsSince(publicationCount));
            Assert.NotEmpty(Fixture.StateStore.State!.Status.PendingTranscriptEvidence!);
            Assert.Empty(Fixture.TranscriptStore.Flushes);
        }
        finally { release.TrySetResult(); }
        await compacting;

        AssertHint(Assert.Single(CanonicalHintsSince(publicationCount)), grain);
        Assert.NotNull(publishedState);
        Assert.Empty(publishedState.Status.PendingTranscriptEvidence!);
        Assert.NotEmpty(Fixture.TranscriptStore.Flushes);
        Assert.Single(Fixture.StateStore.Events, entry => entry.Value is AgentSessionContextCompacted);
        Assert.DoesNotContain(Fixture.TranscriptStore.Flushes.SelectMany(flush => flush.Parts),
            part => part.Type == RuntimeEventTypes.SessionActivity && part.PayloadJson == "{}");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsCanonicalHint(TranscriptEnvelope envelope) =>
        envelope.Type == RuntimeEventTypes.SessionActivity && envelope.RuntimeSessionId is null;

    private TranscriptEnvelope[] CanonicalHintsSince(int publicationCount) =>
        Fixture.TranscriptPublisher.Published.Skip(publicationCount).Where(IsCanonicalHint).ToArray();

    private static void AssertHint(TranscriptEnvelope hint, IAgentSessionGrain grain)
    {
        Assert.Equal(grain.GetPrimaryKeyString(), hint.SessionId);
        Assert.Null(hint.RuntimeSessionId);
        Assert.Equal("opencode", hint.Runtime);
        Assert.Equal(RuntimeEventTypes.SessionActivity, hint.Type);
        Assert.Equal("{}", hint.Payload.GetRawText());
        Assert.True(hint.Sequence > 0);
        Assert.Equal((-hint.Sequence).ToString(), hint.Id);
    }
}
