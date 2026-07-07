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

    protected Task WaitUntilAsync(Func<bool> condition, string description, int timeoutMs = 5000)
        => TestWait.ForAsync(
            condition,
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(25),
            description,
            () =>
            {
                Fixture.TimeProvider.Advance(TimeSpan.FromMilliseconds(250));
                return Task.CompletedTask;
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
    public async Task PersistCallback_StateSaveFailure_LogsErrorAndRetries()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        Fixture.StateStore.NextException = new InvalidOperationException("state store down");

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        await WaitUntilAsync(
            () => Fixture.StateStore.SaveCount >= 2
                && Fixture.TranscriptStore.Flushes.Count >= 2
                && Fixture.Logger.Entries.Any(e => e.Level == LogLevel.Error),
            "agent session state persistence retry");

        Assert.Equal(2, Fixture.StateStore.SaveCount);
        Assert.Equal(2, Fixture.TranscriptStore.Flushes.Count);
        var stateError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(grain.GetPrimaryKeyString(), stateError.Message);
        Assert.Contains("state store down", stateError.Exception?.Message ?? string.Empty);
        var retryFlush = Fixture.TranscriptStore.Flushes[1];
        var part = Assert.Single(retryFlush.Parts);
        Assert.Equal("world", part.TextDelta);
    }
}

[Trait(Traits.Speed.Name, Traits.Speed.Grain)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
public class AgentSessionGrainPersistTranscriptFailureSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionGrainPersistTranscriptFailureSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PersistCallback_TranscriptSaveFailure_LogsErrorAndRetriesWithoutCommit()
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

        await WaitUntilAsync(
            () => Fixture.StateStore.SaveCount >= 3
                && Fixture.TranscriptStore.Flushes.Count >= 1
                && Fixture.Logger.Entries.Any(e => e.Level == LogLevel.Error),
            "agent session transcript persistence retry");

        Assert.Equal(3, Fixture.StateStore.SaveCount);
        var transcriptError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(grain.GetPrimaryKeyString(), transcriptError.Message);
        Assert.Contains("1", transcriptError.Message);
        Assert.Contains("transcript store down", transcriptError.Exception?.Message ?? string.Empty);
        var retryFlush = Assert.Single(Fixture.TranscriptStore.Flushes);
        var part = Assert.Single(retryFlush.Parts);
        Assert.Equal("world", part.TextDelta);
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
    public async Task Deactivation_StateSaveFailure_LogsErrorAndStillFlushesTranscript()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "test"));

        Fixture.StateStore.NextException = new InvalidOperationException("state store down");

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                new AgentSessionRuntimeEventInput("session.input", "{\"text\":\"hello\",\"kind\":\"task\"}"),
                new AgentSessionRuntimeEventInput("message.delta", "{\"text\":\"world\"}")
            }));

        await DeactivateAsync(grain);

        Assert.Single(Fixture.TranscriptStore.Flushes);
        var stateError = Assert.Single(Fixture.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(grain.GetPrimaryKeyString(), stateError.Message);
        Assert.Contains("state store down", stateError.Exception?.Message ?? string.Empty);
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
