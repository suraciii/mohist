using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Sessions;

public class AgentSessionRecoveryDomainSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RebindRuntimeSession_UpdatesBindingAndPreservesSize()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            Phase = AgentSessionStatus.Bound,
            AgentRuntimeSessionId = "acp-old",
            BoundAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            UsageSummary = new AgentUsageSummary
            {
                InputTokens = 100,
                ContextWindowUsed = 90_000,
                ContextWindowSize = 200_000,
            }
        };
        var now = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

        var events = session.RebindRuntimeSession("acp-new", contextWindowUsedAfter: 5_000, contextWindowSizeAfter: 200_000, now);

        Assert.Equal("acp-new", session.Status.AgentRuntimeSessionId);
        Assert.Equal(now, session.Status.BoundAt);
        Assert.Equal(5_000, session.Status.UsageSummary!.ContextWindowUsed);
        Assert.Equal(200_000, session.Status.UsageSummary!.ContextWindowSize);
        var bound = Assert.Single(events, e => e.Value is AgentSessionRuntimeBound);
        Assert.Equal("acp-new", Assert.IsType<AgentSessionRuntimeBound>(bound.Value).AgentRuntimeSessionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RebindRuntimeSession_SameId_DoesNotEmitBoundEvent()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            Phase = AgentSessionStatus.Bound,
            AgentRuntimeSessionId = "acp-keep"
        };

        var events = session.RebindRuntimeSession("acp-keep", null, null, DateTime.UtcNow);

        Assert.Equal("acp-keep", session.Status.AgentRuntimeSessionId);
        Assert.Empty(events);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordCompaction_EmitsCompactionEventWithBeforeAfterAndSummary()
    {
        var session = CreateSession();
        var now = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

        var events = session.RecordCompaction(
            contextWindowUsedBefore: 180_000,
            contextWindowUsedAfter: 50_000,
            contextWindowSize: 200_000,
            strategy: "summary",
            summary: "## Recovery summary",
            now);

        var compaction = Assert.IsType<AgentSessionContextCompacted>(Assert.Single(events).Value);
        Assert.Equal(180_000, compaction.ContextWindowUsedBefore);
        Assert.Equal(50_000, compaction.ContextWindowUsedAfter);
        Assert.Equal(200_000, compaction.ContextWindowSize);
        Assert.Equal("summary", compaction.Strategy);
        Assert.Equal("## Recovery summary", compaction.Summary);
        Assert.Equal(now, compaction.RecordedAt);
        Assert.Equal(50_000, session.Status.UsageSummary!.ContextWindowUsed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordCompaction_NullAfter_PreservesExistingUsage()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            UsageSummary = new AgentUsageSummary
            {
                ContextWindowUsed = 42_000,
                ContextWindowSize = 100_000
            }
        };

        var events = session.RecordCompaction(
            contextWindowUsedBefore: 99_000,
            contextWindowUsedAfter: null,
            contextWindowSize: null,
            strategy: "reset",
            summary: null,
            now: DateTime.UtcNow);

        Assert.Equal(42_000, session.Status.UsageSummary!.ContextWindowUsed);
        Assert.Equal(100_000, session.Status.UsageSummary!.ContextWindowSize);
        var compaction = Assert.IsType<AgentSessionContextCompacted>(Assert.Single(events).Value);
        Assert.Equal("reset", compaction.Strategy);
        Assert.Null(compaction.Summary);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ContextUsagePercent_RoundsAndClamps()
    {
        Assert.Equal(45.0, AgentSessionJsonHelper.ContextUsagePercent(45_000, 100_000));
        Assert.Equal(99.99, AgentSessionJsonHelper.ContextUsagePercent(199_980, 200_000));
        Assert.Equal(100.0, AgentSessionJsonHelper.ContextUsagePercent(2_000, 1_000));
        Assert.Null(AgentSessionJsonHelper.ContextUsagePercent(null, 1_000));
        Assert.Null(AgentSessionJsonHelper.ContextUsagePercent(10, 0));
        Assert.Null(AgentSessionJsonHelper.ContextUsagePercent(0, 0));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ContextUsagePercent_NegativeOrNullValues_ReturnsNull()
    {
        Assert.Null(AgentSessionJsonHelper.ContextUsagePercent(-1, 100));
        Assert.Null(AgentSessionJsonHelper.ContextUsagePercent(50, null));
        Assert.Null(AgentSessionJsonHelper.ContextUsagePercent(null, null));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordContextExhaustion_EmitsDomainEventWithCategoryAndPercent()
    {
        var session = CreateSession();
        var now = new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc);

        var events = session.RecordContextExhaustion(
            failureCategory: "context_exhaustion",
            contextUsagePercent: 94d,
            contextWindowUsed: 940_000,
            contextWindowSize: 1_000_000,
            now);

        var exhaustion = Assert.IsType<AgentSessionContextExhausted>(Assert.Single(events).Value);
        Assert.Equal("context_exhaustion", exhaustion.FailureCategory);
        Assert.Equal(94d, exhaustion.ContextUsagePercent);
        Assert.Equal(940_000, exhaustion.ContextWindowUsed);
        Assert.Equal(1_000_000, exhaustion.ContextWindowSize);
        Assert.Equal(now, exhaustion.RecordedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordContextExhaustion_AcceptsSuspectedCategory()
    {
        var session = CreateSession();

        var events = session.RecordContextExhaustion(
            failureCategory: "context_exhaustion_suspected",
            contextUsagePercent: 88d,
            contextWindowUsed: 88_000,
            contextWindowSize: 100_000,
            now: DateTime.UtcNow);

        var exhaustion = Assert.IsType<AgentSessionContextExhausted>(Assert.Single(events).Value);
        Assert.Equal("context_exhaustion_suspected", exhaustion.FailureCategory);
        Assert.Equal(88d, exhaustion.ContextUsagePercent);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordContextExhaustion_NullValues_ArePreserved()
    {
        // The classifier can call this with nulls when context data
        // is missing — the event must still round-trip.
        var session = CreateSession();

        var events = session.RecordContextExhaustion(
            failureCategory: null,
            contextUsagePercent: null,
            contextWindowUsed: null,
            contextWindowSize: null,
            now: DateTime.UtcNow);

        var exhaustion = Assert.IsType<AgentSessionContextExhausted>(Assert.Single(events).Value);
        Assert.Null(exhaustion.FailureCategory);
        Assert.Null(exhaustion.ContextUsagePercent);
        Assert.Null(exhaustion.ContextWindowUsed);
        Assert.Null(exhaustion.ContextWindowSize);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordContextHealthUpdate_EmitsDomainEventWithStatusAndMetrics()
    {
        var session = CreateSession();
        var now = new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc);

        var events = session.RecordContextHealthUpdate(
            healthStatus: "red",
            contextUsagePercent: 85d,
            contextWindowUsed: 85_000,
            contextWindowSize: 100_000,
            now);

        var health = Assert.IsType<AgentSessionContextHealthUpdated>(Assert.Single(events).Value);
        Assert.Equal("red", health.HealthStatus);
        Assert.Equal(85d, health.ContextUsagePercent);
        Assert.Equal(85_000, health.ContextWindowUsed);
        Assert.Equal(100_000, health.ContextWindowSize);
        Assert.Equal(now, health.RecordedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RecordContextHealthUpdate_AcceptsGreenAndYellow()
    {
        var session = CreateSession();
        var now = DateTime.UtcNow;

        var green = session.RecordContextHealthUpdate("green", 30d, 30_000, 100_000, now);
        var yellow = session.RecordContextHealthUpdate("yellow", 70d, 70_000, 100_000, now);

        Assert.Equal("green", Assert.IsType<AgentSessionContextHealthUpdated>(green[0].Value).HealthStatus);
        Assert.Equal("yellow", Assert.IsType<AgentSessionContextHealthUpdated>(yellow[0].Value).HealthStatus);
    }

    private static AgentSession CreateSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("owner", "proj")
            .WithLabel("source", "wf")
            .WithLabel("name", "session");
        return AgentSession.Create(
            "proj/wf/session",
            "runner-1",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
