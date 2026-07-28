using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Integration tests verifying that the new SSE event types from
/// <c>openspec/changes/issue-110/specs/pipeline-session-events</c>
/// (<c>compaction_event</c> and <c>context_health_update</c>) are
/// emitted through the transcript publisher and persisted in the
/// session stream log when the underlying events fire.
/// </summary>
[Collection("EventPublishing")]
public class AgentSessionContextEventPublishingSpecs
{
    private readonly EventPublishingIntegrationFixture _fixture;
    private readonly string _runnerId = $"ctx-events-{Guid.NewGuid():N}";

    public AgentSessionContextEventPublishingSpecs(EventPublishingIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UsageUpdated_EmitsContextHealthUpdateThroughTranscriptPublisher()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-context"));

        _fixture.RecordingTranscriptPublisher.Clear();

        // First snapshot at 30% (green) seeds the health state.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":300,"contextWindowSize":1000}"""),
            }, RuntimeSessionId: "runtime-context"));

        // Cross green→yellow (60% threshold) at 65%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":650,"contextWindowSize":1000}"""),
            }, RuntimeSessionId: "runtime-context"));

        // Cross yellow→red (80% threshold) at 85%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":850,"contextWindowSize":1000}"""),
            }, RuntimeSessionId: "runtime-context"));

        var healthEvents = _fixture.RecordingTranscriptPublisher.Published
            .Where(e => e.Type == "context_health_update")
            .ToList();

        // At least one yellow and one red snapshot must have been
        // emitted (the initial green seed is also expected).
        Assert.Contains(healthEvents, e =>
        {
            var status = e.Payload.GetProperty("healthStatus").GetString();
            return string.Equals(status, "yellow", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(healthEvents, e =>
        {
            var status = e.Payload.GetProperty("healthStatus").GetString();
            return string.Equals(status, "red", StringComparison.OrdinalIgnoreCase);
        });

        // Each health snapshot carries the current context metrics.
        var red = healthEvents.First(e =>
            string.Equals(e.Payload.GetProperty("healthStatus").GetString(), "red", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(850, red.Payload.GetProperty("contextWindowUsed").GetInt64());
        Assert.Equal(1000, red.Payload.GetProperty("contextWindowSize").GetInt64());
        Assert.Equal(85d, red.Payload.GetProperty("contextUsagePercent").GetDouble());
    }

    [Fact]
    public async Task CompactAsync_EmitsCompactionEventThroughTranscriptPublisher()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-before-compact"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        _fixture.RecordingTranscriptPublisher.Clear();

        await grain.CompactAsync(new CompactAgentSessionCommand(
            Summary: "## Compacted summary"));

        var compactionEvents = _fixture.RecordingTranscriptPublisher.Published
            .Where(e => e.Type == "compaction_event")
            .ToList();
        Assert.Single(compactionEvents);

        var envelope = compactionEvents[0];
        Assert.Equal("summary", envelope.Payload.GetProperty("strategy").GetString());
        Assert.Equal("## Compacted summary", envelope.Payload.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ResetAsync_DoesNotEmitCompactionEvent()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-before-reset"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        _fixture.RecordingTranscriptPublisher.Clear();

        await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "runtime-before-reset",
            ReplacementRuntimeSessionId: "runtime-after-reset"));

        var compactionEvents = _fixture.RecordingTranscriptPublisher.Published
            .Where(e => e.Type == "compaction_event")
            .ToList();
        Assert.Empty(compactionEvents);
    }

    [Fact]
    public async Task SessionClosed_FailedWithContextExhaustion_PersistsContextExhaustedEventRow()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-context"));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        // Bring usage to 96%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":960,"contextWindowSize":1000}"""),
            }, RuntimeSessionId: "runtime-context"));

        // Trigger the exhaustion classification. Under the activity model the
        // terminal-close event (`session.closed`) is a no-op; context-exhaustion
        // classification is now driven by the `turn.failed` runtime event, so
        // emit one against the same near-full context window.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("turn.failed", """{"status":"failed","exitCode":1,"producedArtifacts":false}"""),
            }, RuntimeSessionId: "runtime-context"));

        await persistence.WaitAsync();

        var eventStore = _fixture.Services.GetRequiredService<Mohist.Server.Infrastructure.Events.IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(sessionId);
        var exhaustion = Assert.Single(
            stored,
            s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionContextExhausted);
        Assert.Equal("context_exhaustion", exhaustion.Envelope.Data!.Value.GetProperty("failureCategory").GetString());
        Assert.Equal(96d, exhaustion.Envelope.Data!.Value.GetProperty("contextUsagePercent").GetDouble());
    }

    [Fact]
    public async Task UsageUpdated_EmitsContextHealthUpdatedEventRow()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-context"));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        // Bring usage to 50% (green) — first snapshot.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":500,"contextWindowSize":1000}"""),
            }, RuntimeSessionId: "runtime-context"));

        await persistence.WaitAsync();

        var eventStore = _fixture.Services.GetRequiredService<Mohist.Server.Infrastructure.Events.IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(sessionId);
        var health = Assert.Single(
            stored,
            s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionContextHealthUpdated);
        Assert.Equal("green", health.Envelope.Data!.Value.GetProperty("healthStatus").GetString());
        Assert.Equal(50d, health.Envelope.Data!.Value.GetProperty("contextUsagePercent").GetDouble());
    }
    private OpenAgentSessionCommand OpenCommand() => new(
        _runnerId,
        "opencode",
        WorkDir: "/work",
        Metadata: WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext("project-1", "workflow-1", "build")));
}
