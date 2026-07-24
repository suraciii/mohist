using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentSessionRecoveryDomainTests
{
    [Fact]
    public void RebindRuntimeSession_ReplacesFullBindingAndClearsRuntimeContext()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = "runtime-old",
            UsageSummary = new AgentUsageSummary { InputTokens = 100, ContextWindowUsed = 90_000, ContextWindowSize = 200_000 }
        };
        var expected = session.CurrentRuntimeBinding();

        var events = session.RebindRuntimeSession(
            expected,
            new AgentRuntimeBinding("runner-2", "pi", "runtime-new"),
            "runtime-change",
            TestTime.UtcDateTime);

        Assert.Equal(new AgentRuntimeBinding("runner-2", "pi", "runtime-new"), session.CurrentRuntimeBinding());
        Assert.Equal(100, session.Status.UsageSummary!.InputTokens);
        Assert.Null(session.Status.UsageSummary.ContextWindowUsed);
        Assert.Null(session.Status.UsageSummary.ContextWindowSize);
        Assert.IsType<AgentSessionRuntimeBound>(Assert.Single(events).Value);
    }

    [Fact]
    public void RebindRuntimeSession_RejectsStaleExpectedBindingWithoutMutation()
    {
        var session = CreateSession();
        session.Status = session.Status with { AgentRuntimeSessionId = "runtime-current" };
        var before = session.CurrentRuntimeBinding();

        Assert.Throws<StaleRuntimeSessionBindingException>(() => session.RebindRuntimeSession(
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-stale"),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            "reset",
            TestTime.UtcDateTime));

        Assert.Equal(before, session.CurrentRuntimeBinding());
    }

    [Theory]
    [InlineData(AgentSessionActivity.Active)]
    [InlineData(AgentSessionActivity.Unknown)]
    public void ReconcileMissingBinding_SettlesIdleAndRebinds(AgentSessionActivity activity)
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = "runtime-current",
            Activity = activity,
            UsageSummary = new AgentUsageSummary { InputTokens = 100, ContextWindowUsed = 90_000, ContextWindowSize = 200_000 }
        };
        var expected = session.CurrentRuntimeBinding();

        var events = session.ReconcileMissingBinding(
            expected,
            expected with { RuntimeSessionId = "runtime-replacement" },
            TestTime.UtcDateTime);

        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
        Assert.Equal("runtime-replacement", session.Status.AgentRuntimeSessionId);
        Assert.Equal(100, session.Status.UsageSummary!.InputTokens);
        Assert.Null(session.Status.UsageSummary.ContextWindowUsed);
        Assert.Null(session.Status.UsageSummary.ContextWindowSize);
        Assert.IsType<AgentSessionRuntimeBound>(Assert.Single(events).Value);
    }

    [Fact]
    public void ReconcileMissingBinding_StaleExpectedBindingPreservesActivityAndUsage()
    {
        var session = CreateSession();
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = "runtime-current",
            Activity = AgentSessionActivity.Unknown,
            UsageSummary = new AgentUsageSummary { InputTokens = 100, ContextWindowUsed = 90_000, ContextWindowSize = 200_000 }
        };
        var before = session.Status;

        Assert.Throws<StaleRuntimeSessionBindingException>(() => session.ReconcileMissingBinding(
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-stale"),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-candidate"),
            TestTime.UtcDateTime));

        Assert.Equal(before, session.Status);
    }

    [Theory]
    [InlineData(AgentSessionActivity.Active)]
    [InlineData(AgentSessionActivity.Unknown)]
    public void RebindRuntimeSession_RequiresIdle(AgentSessionActivity activity)
    {
        var session = CreateSession();
        session.Status = session.Status with { AgentRuntimeSessionId = "runtime-current", Activity = activity };

        Assert.Throws<InvalidOperationException>(() => session.RebindRuntimeSession(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            "reset",
            TestTime.UtcDateTime));
    }

    [Fact]
    public void RebindRuntimeSession_RejectsUnknownReason()
    {
        var session = CreateSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => session.RebindRuntimeSession(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            "other",
            TestTime.UtcDateTime));
    }

    private static AgentSession CreateSession() => AgentSession.Create(
        "session-1",
        "runner-1",
        "/work",
        metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"),
        now: TestTime.UtcDateTime,
        runtime: "opencode");
}
