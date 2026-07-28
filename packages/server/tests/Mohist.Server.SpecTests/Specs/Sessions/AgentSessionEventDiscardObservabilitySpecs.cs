using Microsoft.Extensions.Logging;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionEventDiscardObservabilitySpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionEventDiscardObservabilitySpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task StaleBinding_LogsDiscardAndLeavesSessionEffectsUnchanged()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var before = await grain.GetAsync();
        var saveCount = _fixture.StateStore.SaveCount;

        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput> { Event("message.delta"), Event("session.liveness") },
            "runtime-stale"));

        Assert.Empty(result);
        var warning = Assert.Single(DiscardWarnings());
        Assert.Equal(before!.Id, warning.State["SessionId"]);
        Assert.Equal("runtime-current", warning.State["ExpectedRuntimeSessionId"]);
        Assert.Equal("runtime-stale", warning.State["ReportedRuntimeSessionId"]);
        Assert.Equal(2, warning.State["DiscardedEventCount"]);
        Assert.Equal(saveCount, _fixture.StateStore.SaveCount);
        Assert.Equal(before, await grain.GetAsync());
        Assert.Empty(_fixture.TranscriptStore.Flushes);
        Assert.Empty(_fixture.TranscriptPublisher.Published);
    }

    [Fact]
    public async Task MissingBinding_LogsAbsentIdentityAndRejectsWholeBatch()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var saveCount = _fixture.StateStore.SaveCount;

        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput> { Event("message.delta"), Event("message.delta"), Event("session.liveness") },
            ""));

        Assert.Empty(result);
        var warning = Assert.Single(DiscardWarnings());
        Assert.Equal("runtime-current", warning.State["ExpectedRuntimeSessionId"]);
        Assert.Null(warning.State["ReportedRuntimeSessionId"]);
        Assert.Equal(3, warning.State["DiscardedEventCount"]);
        Assert.Equal(saveCount, _fixture.StateStore.SaveCount);
        Assert.Empty(_fixture.TranscriptStore.Flushes);
        Assert.Empty(_fixture.TranscriptPublisher.Published);
    }

    [Fact]
    public async Task RetiredEvents_AreDiscardedBeforeSessionEffects()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var before = await grain.GetAsync();
        var saveCount = _fixture.StateStore.SaveCount;

        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                Event("session.closed"),
                Event("session.followup_completed"),
                Event("session.followup_failed"),
            },
            "runtime-current"));

        Assert.Empty(result);
        Assert.Equal(saveCount, _fixture.StateStore.SaveCount);
        Assert.Equal(before, await grain.GetAsync());
        Assert.Equal(3, DiscardWarnings().Count);
        Assert.Empty(_fixture.TranscriptStore.Flushes);
        Assert.Empty(_fixture.TranscriptPublisher.Published);
    }

    [Fact]
    public async Task MixedBatch_DiscardsRetiredTypesAndProcessesSupportedEvents()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");

        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput>
            {
                Event("message.delta"),
                Event("session.closed"),
                Event("session.followup_completed"),
                Event("session.followup_failed"),
                new(RuntimeEventTypes.SessionActivity, "{\"activity\":\"idle\",\"status\":\"failed\",\"operationId\":\"delivery-1\"}"),
            },
            "runtime-current"));
        await grain.WaitForPersistenceAsync(_fixture.Persistence);

        Assert.Equal(["message.delta", "session.activity"], result.Select(entry => entry.Type));
        Assert.Equal(3, DiscardWarnings().Count);
        Assert.Equal(["message.delta", "session.activity"], _fixture.TranscriptPublisher.Published.Select(entry => entry.Type));
        var flush = Assert.Single(_fixture.TranscriptStore.Flushes);
        var activity = Assert.Single(flush.Parts, part => part.Type == TranscriptPartTypes.SessionActivity);
        Assert.Contains("\"status\":\"failed\"", activity.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(flush.Parts, part => part.Type is "session.closed" or "session.followup_completed" or "session.followup_failed");
    }

    [Fact]
    public async Task SupportedOnlyBatch_DoesNotLogDiscardWarning()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput> { Event("message.delta"), Event("session.liveness") },
            "runtime-current"));

        Assert.Empty(DiscardWarnings());
    }

    private async Task<IAgentSessionGrain> OpenBoundGrainAsync(string runtimeSessionId)
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>($"agent-session-spec-{Guid.NewGuid():N}");
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: WorkflowAgentSessionMetadata.Metadata(
                new WorkflowAgentSessionContext("project-1", "workflow-1", "build"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        return grain;
    }

    private static AgentSessionRuntimeEventInput Event(string type) =>
        new(type, type == "message.delta" ? "{\"text\":\"hello\"}" : "{}");

    private IReadOnlyList<LogEntry> DiscardWarnings() =>
        _fixture.Logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("discarded", StringComparison.Ordinal))
            .ToArray();
}
