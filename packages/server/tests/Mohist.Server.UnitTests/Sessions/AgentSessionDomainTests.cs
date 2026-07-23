using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class AgentSessionDomainTests
{
    private static AgentSession CreateSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "proj")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "wf")
            .WithLabel("mohist.io/session-name", "session");

        var session = AgentSession.Create(
            "proj/wf/session",
            "runner-1",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        session.Settings = new AgentSessionSettings("opencode");
        return session;
    }

    private static AgentUsageSummary Usage(AgentSession session) => session.Status.UsageSummary ?? new AgentUsageSummary();

    [Fact]
    public void Create_OrganizesSessionIntoResourceSections()
    {
        var session = CreateSession();

        Assert.Equal("proj/wf/session", session.Id);
        Assert.Equal("proj", session.Metadata.Label("mohist.io/project-id"));
        Assert.Equal("wf", session.Metadata.Label("mohist.io/source-id"));
        Assert.Equal("session", session.Metadata.Label("mohist.io/session-name"));
        Assert.Null(session.Metadata.Label("work"));
        Assert.Null(session.Metadata.Annotation("title"));
        Assert.Equal("runner-1", session.Runtime.RunnerId);
        Assert.Equal("/work", session.Runtime.WorkDir);
        Assert.Null(session.Status.AgentRuntimeSessionId);
        Assert.Equal(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), session.Status.CreatedAt);
        Assert.NotNull(session.Status.UsageSummary);
    }

    [Fact]
    public void MetadataMerge_PreservesSourceAndAcceptsAnnotationsOnly()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build");

        var merged = metadata.Merge(new AgentSessionMetadata(
            Annotations: new Dictionary<string, string> { ["title"] = "Build" }));

        Assert.Equal("workflow", merged.Label("mohist.io/source-kind"));
        Assert.Equal("Build", merged.Annotation("title"));
        Assert.Throws<InvalidOperationException>(() => metadata.Merge(new AgentSessionMetadata(
            Labels: new Dictionary<string, string> { ["mohist.io/source-kind"] = "agent-launch" })));
    }

    [Fact]
    public void Create_RequiresACompleteKnownSource()
    {
        Assert.Throws<InvalidOperationException>(() => AgentSession.Create(
            "source-required",
            "runner-1",
            "/work",
            metadata: new AgentSessionMetadata(),
            now: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void LegacyMetadata_WithOnlyProjectLabel_RemainsReadable()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1");

        metadata.ValidateSource(allowLegacySource: true);
        Assert.Throws<InvalidOperationException>(() => metadata.ValidateSource());
    }

    [Fact]
    public void StateJson_UsesMetadataRuntimeSettingsAndStatusSections()
    {
        var session = CreateSession();

        session.AttachPhysicalSession("runtime-1", "intent-model", "/work", "/change", 123, TestTime.UtcDateTime);
        session.ApplyUsage(10, 5, 15, 1, 2, 0.01, "USD", 100, 200, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        var json = JsonSerializer.Serialize(session);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("Id", out _));
        Assert.True(root.TryGetProperty("Metadata", out _));
        Assert.True(root.TryGetProperty("Runtime", out _));
        Assert.True(root.TryGetProperty("Settings", out _));
        Assert.True(root.TryGetProperty("Status", out _));
        Assert.False(root.TryGetProperty("ProjectId", out _));
        Assert.False(root.TryGetProperty("IssueNumber", out _));
        Assert.False(root.TryGetProperty("RunId", out _));
        Assert.False(root.TryGetProperty("TaskId", out _));
        Assert.False(root.TryGetProperty("Model", out _));
        Assert.False(root.TryGetProperty("ProcessPid", out _));
        Assert.False(root.TryGetProperty("UsageSummary", out _));

        Assert.True(root.GetProperty("Status").TryGetProperty("UsageSummary", out _));
        Assert.True(root.GetProperty("Settings").TryGetProperty("Model", out _));
    }

    [Fact]
    public void ApplyUsage_AccumulatesTokenCounters()
    {
        var session = CreateSession();

        var firstEvents = session.ApplyUsage(10, 5, 15, 2, 1, 0.001, "USD", 100, 200, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        var secondEvents = session.ApplyUsage(20, 10, 30, 3, 2, 0.002, "USD", 150, 200, new DateTime(2026, 6, 10, 0, 0, 30, DateTimeKind.Utc));

        Assert.Equal(30, Usage(session).InputTokens);
        Assert.Equal(15, Usage(session).OutputTokens);
        Assert.Equal(45, Usage(session).TotalTokens);
        Assert.Equal(5, Usage(session).CachedReadTokens);
        Assert.Equal(3, Usage(session).ThoughtTokens);
        Assert.IsType<AgentSessionUsageRecorded>(Assert.Single(firstEvents).Value);
        Assert.IsType<AgentSessionUsageRecorded>(Assert.Single(secondEvents).Value);
    }

    [Fact]
    public void ApplyUsage_KeepsCacheWriteSeparateFromCacheRead()
    {
        var session = CreateSession();

        session.ApplyUsage(10, 5, 15, 2, 1, 0.001, "USD", null, null, TestTime.UtcDateTime, cachedWriteTokens: 7);
        session.ApplyUsage(null, null, null, 3, null, null, null, null, null, TestTime.UtcDateTime.AddSeconds(1), cachedWriteTokens: 11);

        Assert.Equal(5, Usage(session).CachedReadTokens);
        Assert.Equal(18, Usage(session).CachedWriteTokens);
    }

    [Fact]
    public void AttachPhysicalSession_FirstBinding_ReturnsStartedAndModelChangedEvents()
    {
        var session = CreateSession();

        var events = session.AttachPhysicalSession("runtime-session-1", "model-a", "/work", null, null, TestTime.UtcDateTime);

        Assert.Collection(events,
            e => Assert.Equal("runtime-session-1", Assert.IsType<AgentSessionRuntimeBound>(e.Value).AgentRuntimeSessionId),
            e => Assert.Equal("model-a", Assert.IsType<AgentSessionModelChanged>(e.Value).Model));
    }

    [Fact]
    public void AttachPhysicalSession_SamePhysicalSession_IsIdempotent()
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-session-1", "model-a", "/work", null, null, TestTime.UtcDateTime);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            session.AttachPhysicalSession("runtime-session-1", "model-a", "/other", null, null, TestTime.UtcDateTime));

        Assert.Contains("work directory", exception.Message, StringComparison.Ordinal);
        Assert.Equal("runtime-session-1", session.Status.AgentRuntimeSessionId);
        Assert.Equal("/work", session.Runtime.WorkDir);
    }

    [Fact]
    public void RuntimeActivity_AfterClosedObservation_CanContinue()
    {
        var session = CreateSession();
        var first = new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);
        var second = first.AddMinutes(1);

        session.RecordActivity(first);
        session.ApplyUsage(10, 5, 15, null, null, null, null, null, null, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        session.RecordActivity(second);

        Assert.Equal(second, session.Status.LastDataAt);
        Assert.Equal(10, Usage(session).InputTokens);
    }

    [Fact]
    public void ApplyUsage_AccumulatesCostAndUpdatesCurrency()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, 0.001, "USD", null, null, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        session.ApplyUsage(null, null, null, null, null, 0.002, "EUR", null, null, new DateTime(2026, 6, 10, 0, 0, 30, DateTimeKind.Utc));

        Assert.Equal(0.003, Usage(session).CostAmount);
        Assert.Equal("EUR", Usage(session).CostCurrency);
    }

    [Fact]
    public void ApplyUsage_UpdatesContextWindowSnapshot()
    {
        var session = CreateSession();
        var firstAt = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var secondAt = firstAt.AddMinutes(1);

        session.ApplyUsage(null, null, null, null, null, null, null, 100, 200, firstAt);
        session.ApplyUsage(null, null, null, null, null, null, null, 150, 250, secondAt);

        Assert.Equal(150, Usage(session).ContextWindowUsed);
        Assert.Equal(250, Usage(session).ContextWindowSize);
    }

    [Fact]
    public void ApplyUsage_NullDelta_DoesNotChangeExistingValues()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            UsageSummary = Usage(session) with
            {
                InputTokens = 10,
                CostAmount = 0.005,
                ContextWindowUsed = 100
            }
        };

        session.ApplyUsage(null, null, null, null, null, null, null, null, null, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Equal(0.005, Usage(session).CostAmount);
        Assert.Equal(100, Usage(session).ContextWindowUsed);
    }

    [Fact]
    public void ApplyUsage_NegativeDelta_IgnoresDelta()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            UsageSummary = Usage(session) with { InputTokens = 10 }
        };

        session.ApplyUsage(-5, -3, -8, null, null, -0.001, null, null, null, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Null(Usage(session).OutputTokens);
        Assert.Null(Usage(session).TotalTokens);
        Assert.Null(Usage(session).CostAmount);
    }

    [Fact]
    public void ApplyUsage_AfterRuntimeCloseObservation_StillMutates()
    {
        var session = CreateSession();
        session.RecordActivity(TestTime.UtcDateTime);

        session.ApplyUsage(10, 5, 15, null, null, 0.001, "USD", 100, 200, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Equal(0.001, Usage(session).CostAmount);
    }

    [Fact]
    public void ApplyUsage_AppendsContextUsageHistorySample()
    {
        var session = CreateSession();
        var now = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

        session.ApplyUsage(null, null, null, null, null, null, null, 25_000, 100_000, now);

        Assert.NotNull(session.Status.ContextUsageHistory);
        var sample = Assert.Single(session.Status.ContextUsageHistory!);
        Assert.Equal(now, sample.At);
        Assert.Equal(25.0, sample.Percent);
    }

    [Fact]
    public void ApplyUsage_HistoryTimeThinningCoalescesNearbySamples()
    {
        var session = CreateSession();
        var bucketStart = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var sameBucketLater = bucketStart.AddSeconds(10);
        var sameBucketLatest = bucketStart.AddSeconds(25);
        var nextBucket = bucketStart.AddSeconds(45);

        session.ApplyUsage(null, null, null, null, null, null, null, 10_000, 100_000, bucketStart);
        session.ApplyUsage(null, null, null, null, null, null, null, 20_000, 100_000, sameBucketLater);
        session.ApplyUsage(null, null, null, null, null, null, null, 30_000, 100_000, sameBucketLatest);
        session.ApplyUsage(null, null, null, null, null, null, null, 40_000, 100_000, nextBucket);

        var history = session.Status.ContextUsageHistory!;
        // 1st sample coalesced by 2nd (same bucket), then coalesced again by
        // 3rd (still same bucket); 4th lands in the next bucket and is appended.
        Assert.Equal(2, history.Count);
        Assert.Equal(sameBucketLatest, history[0].At);
        Assert.Equal(30.0, history[0].Percent);
        Assert.Equal(nextBucket, history[1].At);
        Assert.Equal(40.0, history[1].Percent);
    }

    [Fact]
    public void ApplyUsage_HistoryHardCapRetainsLastNSamples()
    {
        var session = CreateSession();
        var now = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        const int total = AgentSessionExtensions.ContextUsageHistoryCap + 5;

        for (var i = 0; i < total; i++)
        {
            var at = now.AddSeconds(i * AgentSessionExtensions.ContextUsageHistoryBucket.TotalSeconds);
            session.ApplyUsage(null, null, null, null, null, null, null, (i + 1) * 1_000, 100_000, at);
        }

        var history = session.Status.ContextUsageHistory!;
        Assert.Equal(AgentSessionExtensions.ContextUsageHistoryCap, history.Count);
        // Cap drops oldest samples — the first retained entry is sample #5
        // (zero-based), which corresponds to 6_000 used / 100_000 size = 6%.
        Assert.Equal(6.0, history[0].Percent);
        Assert.Equal(total * 1_000 / 1000d, history[^1].Percent);
    }

    [Fact]
    public void ApplyUsage_MissingContextWindow_DoesNotAppendSample()
    {
        var session = CreateSession();
        var now = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

        session.ApplyUsage(null, null, null, null, null, null, null, null, null, now);

        Assert.Empty(session.Status.ContextUsageHistory!);
    }

}
