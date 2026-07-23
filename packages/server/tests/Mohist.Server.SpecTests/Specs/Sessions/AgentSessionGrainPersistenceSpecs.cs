using Microsoft.Extensions.Logging;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class AgentSessionGrainPersistenceSpecsBase : IClassFixture<AgentSessionGrainFixture>
{
    protected readonly AgentSessionGrainFixture Fixture;

    protected AgentSessionGrainPersistenceSpecsBase(AgentSessionGrainFixture fixture)
    {
        Fixture = fixture;
        Fixture.Reset();
    }

    protected IAgentSessionGrain NewGrain() => Fixture.Grains.GetGrain<IAgentSessionGrain>($"agent-session-spec-{Guid.NewGuid():N}");

    protected async Task<IAgentSessionGrain> OpenBoundGrainAsync(string runtime = "test")
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open(runtime));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1"));
        return grain;
    }

    protected OpenAgentSessionCommand Open(string runtime = "test") => new(
        "runner-1",
        runtime,
        WorkDir: "/work",
        Metadata: WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext("project-1", "workflow-1", "build")));

    protected async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }
}

public class AgentSessionGrainPersistSuccessSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistSuccessSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FlushForTestAsync_SavesBoundRuntimeEventsAndTranscript()
    {
        var grain = await OpenBoundGrainAsync();

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }, "runtime-1"));

        await grain.FlushForTestAsync();

        Assert.Equal(3, Fixture.StateStore.SaveCount);
        Assert.Single(Fixture.TranscriptStore.Flushes);
        Assert.Single(Fixture.TranscriptStore.Flushes[0].Parts);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\" again\"}")
            }, "runtime-1"));

        await grain.FlushForTestAsync();

        Assert.Equal(4, Fixture.StateStore.SaveCount);
        Assert.Equal(2, Fixture.TranscriptStore.Flushes.Count);
        Assert.Single(Fixture.TranscriptStore.Flushes[1].Parts);
        Assert.Contains(Fixture.TranscriptStore.Flushes[1].Parts, p => p.TextDelta == " again");
    }
}

public class AgentSessionGrainPersistStateFailureSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistStateFailureSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FlushForTestAsync_StateSaveFailure_PropagatesAndQuarantinesActivation()
    {
        // A failed event-aware save must propagate and quarantine the
        // activation: the store's transaction rolled back, but the live
        // session already absorbed the runtime activity. The dirty in-memory
        // state must not be salvaged through a second save on the same
        // activation — the grain deactivates and the next call reloads from
        // storage. (The "same activation rejects further work" guarantee is
        // covered by IssueGrainEventSaveFailureSpecs, which constructs the
        // grain directly so DeactivateOnIdle does not reload it.)
        var grain = await OpenBoundGrainAsync();

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }, "runtime-1"));

        Fixture.StateStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("state store down"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.FlushForTestAsync());
        Assert.Contains("state store down", ex.Message);
        // The faulted save did not increment the count (ThrowIfPending fires
        // before SaveCount++), and the dirty state was not salvaged by the
        // failing flush.
        Assert.Equal(2, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);

        var stateError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("failed to save state", stateError.Message);
        Assert.Contains("state store down", stateError.Exception?.Message ?? string.Empty);
    }
}

public class AgentSessionGrainPersistTranscriptFailureSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistTranscriptFailureSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FlushForTestAsync_TranscriptSaveFailure_RetriesOnlyTranscriptWithoutDuplicateEvents()
    {
        // State/event and transcript retry states are split: a transcript
        // save failure happens AFTER the state/event transaction commits, so
        // the next flush must retry only the transcript and never re-save
        // state (which would re-append already-committed lifecycle events).
        var grain = await OpenBoundGrainAsync();

        Fixture.TranscriptStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("transcript store down"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }, "runtime-1"));

        await grain.FlushForTestAsync();

        // State/event committed on the first flush; no second state save.
        Assert.Equal(3, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);

        await grain.FlushForTestAsync();

        // SaveCount must stay at 2: the retry is transcript-only.
        Assert.Equal(3, Fixture.StateStore.SaveCount);
        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("failed to save transcript", transcriptError.Message);
        Assert.Contains("1", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);
        var retryFlush = Assert.Single(Fixture.TranscriptStore.Flushes);
        var part = Assert.Single(retryFlush.Parts);
        Assert.Equal("world", part.TextDelta);
    }
}

public class AgentSessionGrainRecoveryTranscriptFailureSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainRecoveryTranscriptFailureSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CompactAsync_TranscriptSaveFailure_SchedulesTranscriptOnlyRetry()
    {
        // When the recovery state/event transaction commits but the transcript
        // save fails, the recovery domain fact is durable and the command
        // succeeds. The pending transcript must still reach durable storage:
        // PersistRecoveryAsync schedules the persistence timer so a
        // transcript-only retry fires even on an idle session, and that retry
        // must NOT re-append the committed recovery domain event.
        var grain = NewGrain();
        var opened = await grain.OpenAsync(Open("opencode"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-before-compact"));
        Fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var openedSaveCount = Fixture.StateStore.SaveCount;
        var attachEventCount = Fixture.StateStore.Events.Count;

        Fixture.TranscriptStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("transcript store down"));

        var result = await grain.CompactAsync(
            new CompactAgentSessionCommand(Summary: "s"));

        // The recovery command succeeds; the compaction domain event committed.
        Assert.Equal(opened.Id, result.Id);
        Assert.True(result.WasCompacted);

        // Exactly one recovery save so far (the event-aware commit); the
        // transcript flush failed and is pending retry.
        Assert.Equal(openedSaveCount + 1, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);
        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("durable transcript evidence", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);

        // The pending recovery transcript must reach durable storage even on
        // an idle session. The scheduled persistence timer (PersistTimerDueTime
        // = 200ms) fires a transcript-only retry, and deactivation flushes any
        // remaining pending transcript. Either way the recovery evidence is
        // durable. The core data-safety invariant: the committed recovery
        // domain event was appended exactly once, never re-appended by the
        // transcript retry.
        await DeactivateAsync(grain);

        Assert.NotEmpty(Fixture.TranscriptStore.Flushes);
        var recoveryEvents = Fixture.StateStore.Events
            .Count(e => e is AgentSessionContextCompacted);
        Assert.Equal(1, recoveryEvents);
        Assert.Equal(attachEventCount + 1, Fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task CompactAsync_RepeatedTranscriptFailure_PersistsEvidenceAcrossDeactivation()
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open("opencode"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-durable-evidence"));
        Fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        Fixture.TranscriptStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("first transcript failure"));
        await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "durable"));

        // The deactivation flush can fail too. The pending evidence must remain
        // in persisted session state instead of relying on the disposed timer.
        Fixture.TranscriptStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("second transcript failure"));
        await DeactivateAsync(grain);

        await grain.FlushForTestAsync();

        Assert.Equal(2, Fixture.TranscriptStore.Flushes.Count);
        Assert.All(Fixture.TranscriptStore.Flushes, flush => Assert.Single(flush.Parts));
        Assert.Single(Fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);
        Assert.Empty(Fixture.StateStore.State!.Status.PendingTranscriptEvidence!);
    }
}

public class AgentSessionGrainDeactivationSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainDeactivationSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Deactivation_FlushesPendingStateAndTranscript()
    {
        var grain = await OpenBoundGrainAsync();

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }, "runtime-1"));

        await DeactivateAsync(grain);

        Assert.Equal(3, Fixture.StateStore.SaveCount);
        Assert.Single(Fixture.TranscriptStore.Flushes);
        Assert.Single(Fixture.TranscriptStore.Flushes[0].Parts);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Deactivation_NoPendingData_DoesNotFlushAgain()
    {
        var grain = NewGrain();
        await grain.OpenAsync(Open());

        await DeactivateAsync(grain);

        Assert.Equal(1, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Deactivation_TranscriptSaveFailure_LogsError()
    {
        var grain = await OpenBoundGrainAsync();

        Fixture.TranscriptStore.FailNextSave(
            grain.GetPrimaryKeyString(),
            new InvalidOperationException("transcript store down"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }, "runtime-1"));

        await DeactivateAsync(grain);

        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(grain.GetPrimaryKeyString(), transcriptError.Message);
        Assert.Contains("1", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);
    }
}
