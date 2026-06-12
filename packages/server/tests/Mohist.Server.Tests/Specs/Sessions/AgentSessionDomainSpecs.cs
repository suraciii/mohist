using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Sessions;

public class AgentSessionDomainSpecs
{
    private static AgentSession CreateSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("owner", "proj")
            .WithLabel("source", "wf")
            .WithLabel("name", "session");

        return AgentSession.Create(
            "proj/wf/session",
            "runner-1",
            "opencode",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
    }

    private static AgentUsageSummary Usage(AgentSession session) => session.Status.UsageSummary ?? new AgentUsageSummary();

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void Create_OrganizesSessionIntoResourceSections()
    {
        var session = CreateSession();

        Assert.Equal("proj/wf/session", session.Id);
        Assert.Equal("proj", session.Metadata.Label("owner"));
        Assert.Equal("wf", session.Metadata.Label("source"));
        Assert.Equal("session", session.Metadata.Label("name"));
        Assert.Null(session.Metadata.Label("work"));
        Assert.Null(session.Metadata.Annotation("title"));
        Assert.Equal("runner-1", session.Runtime.RunnerId);
        Assert.Equal("opencode", session.Runtime.AgentRuntime);
        Assert.Equal("/work", session.Runtime.WorkDir);
        Assert.Equal(AgentSessionStatus.Opened, session.Status.Phase);
        Assert.Equal(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), session.Status.CreatedAt);
        Assert.NotNull(session.Status.UsageSummary);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void StateJson_UsesMetadataRuntimeSettingsAndStatusSections()
    {
        var session = CreateSession();

        session.AttachPhysicalSession("acp-1", "intent-model", "/work", "/change", 123, DateTime.UtcNow);
        session.ApplyUsage(10, 5, 15, 1, 2, 0.01, "USD", 100, 200);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ApplyUsage_AccumulatesTokenCounters()
    {
        var session = CreateSession();

        var firstEvents = session.ApplyUsage(10, 5, 15, 2, 1, 0.001, "USD", 100, 200);
        var secondEvents = session.ApplyUsage(20, 10, 30, 3, 2, 0.002, "USD", 150, 200);

        Assert.Equal(30, Usage(session).InputTokens);
        Assert.Equal(15, Usage(session).OutputTokens);
        Assert.Equal(45, Usage(session).TotalTokens);
        Assert.Equal(5, Usage(session).CachedReadTokens);
        Assert.Equal(3, Usage(session).ThoughtTokens);
        Assert.IsType<AgentSessionUsageRecorded>(Assert.Single(firstEvents).Value);
        Assert.IsType<AgentSessionUsageRecorded>(Assert.Single(secondEvents).Value);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void AttachPhysicalSession_FirstBinding_ReturnsStartedAndModelChangedEvents()
    {
        var session = CreateSession();

        var events = session.AttachPhysicalSession("runtime-session-1", "model-a", "/work", null, null, DateTime.UtcNow);

        Assert.Collection(events,
            e => Assert.Equal("runtime-session-1", Assert.IsType<AgentSessionRuntimeBound>(e.Value).AgentRuntimeSessionId),
            e => Assert.Equal("model-a", Assert.IsType<AgentSessionModelChanged>(e.Value).Model));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void AttachPhysicalSession_SamePhysicalSession_IsIdempotent()
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-session-1", "model-a", "/work", null, null, DateTime.UtcNow);

        var events = session.AttachPhysicalSession("runtime-session-1", "model-a", "/other", null, null, DateTime.UtcNow);

        Assert.Empty(events);
        Assert.Equal("runtime-session-1", session.Status.AgentRuntimeSessionId);
        Assert.Equal("/work", session.Runtime.WorkDir);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void AttachPhysicalSession_DifferentPhysicalSession_Throws()
    {
        var session = CreateSession();
        session.AttachPhysicalSession("runtime-session-1", "model-a", "/work", null, null, DateTime.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.AttachPhysicalSession("runtime-session-2", "model-a", "/work", null, null, DateTime.UtcNow));
        Assert.Contains("already attached", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void RuntimeActivity_AfterClosedObservation_CanContinue()
    {
        var session = CreateSession();
        var first = new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);
        var second = first.AddMinutes(1);

        session.RecordActivity(first);
        session.ApplyUsage(10, 5, 15, null, null, null, null, null, null);
        session.RecordActivity(second);

        Assert.Equal(second, session.Status.LastDataAt);
        Assert.Equal(10, Usage(session).InputTokens);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ApplyUsage_AccumulatesCostAndUpdatesCurrency()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, 0.001, "USD", null, null);
        session.ApplyUsage(null, null, null, null, null, 0.002, "EUR", null, null);

        Assert.Equal(0.003, Usage(session).CostAmount);
        Assert.Equal("EUR", Usage(session).CostCurrency);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ApplyUsage_UpdatesContextWindowSnapshot()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, null, null, 100, 200);
        session.ApplyUsage(null, null, null, null, null, null, null, 150, 250);

        Assert.Equal(150, Usage(session).ContextWindowUsed);
        Assert.Equal(250, Usage(session).ContextWindowSize);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

        session.ApplyUsage(null, null, null, null, null, null, null, null, null);

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Equal(0.005, Usage(session).CostAmount);
        Assert.Equal(100, Usage(session).ContextWindowUsed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ApplyUsage_NegativeDelta_IgnoresDelta()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            UsageSummary = Usage(session) with { InputTokens = 10 }
        };

        session.ApplyUsage(-5, -3, -8, null, null, -0.001, null, null, null);

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Null(Usage(session).OutputTokens);
        Assert.Null(Usage(session).TotalTokens);
        Assert.Null(Usage(session).CostAmount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void ApplyUsage_AfterRuntimeCloseObservation_StillMutates()
    {
        var session = CreateSession();
        session.RecordActivity(DateTime.UtcNow);

        session.ApplyUsage(10, 5, 15, null, null, 0.001, "USD", 100, 200);

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Equal(0.001, Usage(session).CostAmount);
    }

}
