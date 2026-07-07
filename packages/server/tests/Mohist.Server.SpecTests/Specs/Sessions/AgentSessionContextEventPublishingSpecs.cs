using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task UsageUpdated_EmitsContextHealthUpdateThroughTranscriptPublisher()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        _fixture.RecordingTranscriptPublisher.Clear();

        // First snapshot at 30% (green) seeds the health state.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":300,"contextWindowSize":1000}"""),
            }));

        // Cross green→yellow (60% threshold) at 65%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":650,"contextWindowSize":1000}"""),
            }));

        // Cross yellow→red (80% threshold) at 85%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":850,"contextWindowSize":1000}"""),
            }));

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactAsync_EmitsCompactionEventThroughTranscriptPublisher()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        _fixture.RecordingTranscriptPublisher.Clear();

        await grain.CompactAsync(new CompactAgentSessionCommand(
            NewAgentSessionId: "acp-after-compact",
            Summary: "## Compacted summary"));

        var compactionEvents = _fixture.RecordingTranscriptPublisher.Published
            .Where(e => e.Type == "compaction_event")
            .ToList();
        Assert.Single(compactionEvents);

        var envelope = compactionEvents[0];
        Assert.Equal("summary", envelope.Payload.GetProperty("strategy").GetString());
        Assert.Equal("## Compacted summary", envelope.Payload.GetProperty("summary").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResetAsync_EmitsCompactionEventWithoutSummary()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        _fixture.RecordingTranscriptPublisher.Clear();

        await grain.ResetAsync(new ResetAgentSessionCommand(NewAgentSessionId: "acp-after-reset"));

        var compactionEvents = _fixture.RecordingTranscriptPublisher.Published
            .Where(e => e.Type == "compaction_event")
            .ToList();
        Assert.Single(compactionEvents);
        var envelope = compactionEvents[0];
        Assert.Equal("reset", envelope.Payload.GetProperty("strategy").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task SessionClosed_FailedWithContextExhaustion_EmitsReverseDnsDomainEvent()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        _fixture.RecordingPublisher.Clear();

        // Bring usage to 96%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":960,"contextWindowSize":1000}"""),
            }));

        // Trigger the exhaustion classification.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("session.closed", """{"status":"failed","exitCode":1}"""),
            }));

        // The domain bus should have received the reverse-DNS
        // AgentSessionContextExhausted event with the usage
        // percent at failure time.
        var exhaustion = _fixture.RecordingPublisher.Published
            .FirstOrDefault(p => p.Type == EventCatalog.ReverseDns.AgentSessionContextExhausted);
        Assert.NotNull(exhaustion);
        Assert.NotNull(exhaustion!.Data);
        Assert.Equal("context_exhaustion", exhaustion.Data!.Value.GetProperty("failureCategory").GetString());
        Assert.Equal(96d, exhaustion.Data!.Value.GetProperty("contextUsagePercent").GetDouble());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task UsageUpdated_EmitsReverseDnsContextHealthUpdatedEvent()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        _fixture.RecordingPublisher.Clear();

        // Bring usage to 50% (green) — first snapshot.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":500,"contextWindowSize":1000}"""),
            }));

        var health = _fixture.RecordingPublisher.Published
            .FirstOrDefault(p => p.Type == EventCatalog.ReverseDns.AgentSessionContextHealthUpdated);
        Assert.NotNull(health);
        Assert.NotNull(health!.Data);
        Assert.Equal("green", health.Data!.Value.GetProperty("healthStatus").GetString());
        Assert.Equal(50d, health.Data!.Value.GetProperty("contextUsagePercent").GetDouble());
    }
}
