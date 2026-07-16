using Microsoft.Extensions.Logging;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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

    protected Task WaitUntilAsync(
        IAgentSessionGrain grain,
        Func<bool> condition,
        string description,
        int timeoutMs = 5000)
        => TestWait.ForAsync(
            condition,
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(25),
            description,
            async () =>
            {
                Fixture.TimeProvider.Advance(TimeSpan.FromMilliseconds(250));
                await grain.GetAsync();
            });
}

[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
public class AgentSessionGrainPersistSuccessSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistSuccessSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PersistCallback_Success_SavesStateAndTranscriptAndDisposesTimer()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        await WaitUntilAsync(
            grain,
            () => Fixture.StateStore.SaveCount >= 2 && Fixture.TranscriptStore.Flushes.Count >= 1,
            "initial agent session persistence");

        Assert.Equal(2, Fixture.StateStore.SaveCount);
        Assert.Single(Fixture.TranscriptStore.Flushes);
        Assert.Single(Fixture.TranscriptStore.Flushes[0].Parts);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\" again\"}")
            }));

        await WaitUntilAsync(
            grain,
            () => Fixture.StateStore.SaveCount >= 3 && Fixture.TranscriptStore.Flushes.Count >= 2,
            "subsequent agent session persistence");

        Assert.Equal(3, Fixture.StateStore.SaveCount);
        Assert.Equal(2, Fixture.TranscriptStore.Flushes.Count);
        Assert.Single(Fixture.TranscriptStore.Flushes[1].Parts);
        Assert.Contains(Fixture.TranscriptStore.Flushes[1].Parts, p => p.TextDelta == " again");
    }
}

[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        Fixture.StateStore.NextException = new InvalidOperationException("state store down");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.FlushForTestAsync());
        Assert.Contains("state store down", ex.Message);
        // The faulted save did not increment the count (ThrowIfPending fires
        // before SaveCount++), and the dirty state was not salvaged by the
        // failing flush.
        Assert.Equal(1, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);

        var stateError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("failed to save state", stateError.Message);
        Assert.Contains("state store down", stateError.Exception?.Message ?? string.Empty);
    }
}

[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        Fixture.TranscriptStore.NextException = new InvalidOperationException("transcript store down");

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        await grain.FlushForTestAsync();

        // State/event committed on the first flush; no second state save.
        Assert.Equal(2, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);

        await grain.FlushForTestAsync();

        // SaveCount must stay at 2: the retry is transcript-only.
        Assert.Equal(2, Fixture.StateStore.SaveCount);
        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("failed to save transcript", transcriptError.Message);
        Assert.Contains("1", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);
        var retryFlush = Assert.Single(Fixture.TranscriptStore.Flushes);
        var part = Assert.Single(retryFlush.Parts);
        Assert.Equal("world", part.TextDelta);
    }
}

[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
public class AgentSessionGrainRecoveryTranscriptFailureSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainRecoveryTranscriptFailureSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CompactAsync_TranscriptSaveFailure_CommitsRecoveryOnceAndDoesNotRetry()
    {
        // Recovery (Compact/Reset) state/event transaction and transcript
        // evidence share the FlushAsync split: when state/events commit but
        // transcript save fails, the recovery domain fact is durable and the
        // command must succeed so a client retry does not re-append duplicate
        // recovery events. The transcript flush stays pending for the next
        // flush cycle.
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));
        var openedSaveCount = Fixture.StateStore.SaveCount;

        Fixture.TranscriptStore.NextException = new InvalidOperationException("transcript store down");

        var result = await grain.CompactAsync(
            new CompactAgentSessionCommand(NewAgentSessionId: "acp-after-compact", Summary: "s"));

        // The recovery command succeeds even though the transcript failed:
        // the rebind/compaction domain events committed atomically.
        Assert.Equal("acp-after-compact", result.AgentSessionId);
        Assert.True(result.WasCompacted);

        // Exactly one recovery save happened (the event-aware commit). A
        // duplicate would require a second recovery save, which does not
        // occur because the command did not throw.
        Assert.Equal(openedSaveCount + 1, Fixture.StateStore.SaveCount);

        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("recovery transcript", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);
    }
}

[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
public class AgentSessionGrainDeactivationSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainDeactivationSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Deactivation_FlushesPendingStateAndTranscript()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        await DeactivateAsync(grain);

        Assert.Equal(2, Fixture.StateStore.SaveCount);
        Assert.Single(Fixture.TranscriptStore.Flushes);
        Assert.Single(Fixture.TranscriptStore.Flushes[0].Parts);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Deactivation_NoPendingData_DoesNotFlushAgain()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        await DeactivateAsync(grain);

        Assert.Equal(1, Fixture.StateStore.SaveCount);
        Assert.Empty(Fixture.TranscriptStore.Flushes);
        Assert.DoesNotContain(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Deactivation_TranscriptSaveFailure_LogsError()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        Fixture.TranscriptStore.NextException = new InvalidOperationException("transcript store down");

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        await DeactivateAsync(grain);

        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(grain.GetPrimaryKeyString(), transcriptError.Message);
        Assert.Contains("1", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);
    }

    private async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }
}
