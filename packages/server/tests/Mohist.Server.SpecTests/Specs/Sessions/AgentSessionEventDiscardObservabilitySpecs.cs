using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
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
        var sessionId = grain.GetPrimaryKeyString();
        var before = await grain.GetAsync();
        var saveCount = _fixture.StateStore.SaveCount;

        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput> { Event("message.delta"), Event("session.liveness") },
            "runtime-stale"));

        Assert.Empty(result);
        var warning = Assert.Single(DiscardWarnings(sessionId));
        Assert.Equal(before!.Id, warning.State["SessionId"]);
        Assert.Equal("runtime-current", warning.State["ExpectedRuntimeSessionId"]);
        Assert.Equal("runtime-stale", warning.State["ReportedRuntimeSessionId"]);
        Assert.Equal(2, warning.State["DiscardedEventCount"]);
        Assert.Equal(saveCount, _fixture.StateStore.SaveCount);
        Assert.Equal(before, await grain.GetAsync());
        Assert.Empty(FlushesFor(sessionId));
        Assert.Empty(PublishedFor(sessionId));
    }

    [Fact]
    public async Task MissingBinding_LogsAbsentIdentityAndRejectsWholeBatch()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var sessionId = grain.GetPrimaryKeyString();
        var saveCount = _fixture.StateStore.SaveCount;

        var result = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput> { Event("message.delta"), Event("message.delta"), Event("session.liveness") },
            ""));

        Assert.Empty(result);
        var warning = Assert.Single(DiscardWarnings(sessionId));
        Assert.Equal("runtime-current", warning.State["ExpectedRuntimeSessionId"]);
        Assert.Null(warning.State["ReportedRuntimeSessionId"]);
        Assert.Equal(3, warning.State["DiscardedEventCount"]);
        Assert.Equal(saveCount, _fixture.StateStore.SaveCount);
        Assert.Empty(FlushesFor(sessionId));
        Assert.Empty(PublishedFor(sessionId));
    }

    [Fact]
    public async Task RetiredEvents_AreDiscardedBeforeSessionEffects()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var sessionId = grain.GetPrimaryKeyString();
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
        Assert.Equal(3, DiscardWarnings(sessionId).Count);
        Assert.Empty(FlushesFor(sessionId));
        Assert.Empty(PublishedFor(sessionId));
    }

    [Fact]
    public async Task MixedBatch_DiscardsRetiredTypesAndProcessesSupportedEvents()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var sessionId = grain.GetPrimaryKeyString();
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        var activityFlush = _fixture.TranscriptStore.WaitForAsync(
            flush => flush.Turn.SessionId == sessionId
                && flush.Parts.Any(part => part.Type == TranscriptPartTypes.SessionActivity));

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
        await persistence.WaitAsync();

        Assert.Equal(["message.delta", "session.activity"], result.Select(entry => entry.Type));
        Assert.Equal(3, DiscardWarnings(sessionId).Count);
        Assert.Equal(["message.delta", "session.activity"], PublishedFor(sessionId).Select(entry => entry.Type));
        var flush = await activityFlush;
        var activity = Assert.Single(flush.Parts, part => part.Type == TranscriptPartTypes.SessionActivity);
        Assert.Contains("\"status\":\"failed\"", activity.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(flush.Parts, part => part.Type is "session.closed" or "session.followup_completed" or "session.followup_failed");
    }

    [Fact]
    public async Task SupportedOnlyBatch_DoesNotLogDiscardWarning()
    {
        var grain = await OpenBoundGrainAsync("runtime-current");
        var sessionId = grain.GetPrimaryKeyString();

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new List<AgentSessionRuntimeEventInput> { Event("message.delta"), Event("session.liveness") },
            "runtime-current"));

        Assert.Empty(DiscardWarnings(sessionId));
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

    private IReadOnlyList<AgentSessionTranscriptFlush> FlushesFor(string sessionId) =>
        _fixture.TranscriptStore.Flushes
            .Where(flush => flush.Turn.SessionId == sessionId)
            .ToArray();

    private IReadOnlyList<TranscriptEnvelope> PublishedFor(string sessionId) =>
        _fixture.TranscriptPublisher.Published
            .Where(envelope => envelope.SessionId == sessionId)
            .ToArray();

    private IReadOnlyList<LogEntry> DiscardWarnings(string sessionId) =>
        _fixture.Logger.Entries
            .Where(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("discarded", StringComparison.Ordinal) &&
                string.Equals(entry.State.GetValueOrDefault("SessionId") as string, sessionId, StringComparison.Ordinal))
            .ToArray();
}
