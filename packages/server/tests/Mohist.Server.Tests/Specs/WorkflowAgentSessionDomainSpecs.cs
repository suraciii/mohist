using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowAgentSessionDomainSpecs
{
    private static WorkflowAgentSession CreateSession() =>
        WorkflowAgentSession.Create(
            "proj/wf/session",
            "proj",
            1,
            "wf",
            "session",
            "runner-1");

    [Fact]
    public void UpdateResolvedModel_SetsResolvedModelOnly()
    {
        var session = CreateSession();
        session.Model = "intent-model";

        session.UpdateResolvedModel("resolved-model");

        Assert.Equal("resolved-model", session.ResolvedModel);
        Assert.Equal("intent-model", session.Model);
    }

    [Fact]
    public void UpdateResolvedModel_Null_DoesNotChangeResolvedModel()
    {
        var session = CreateSession();
        session.ResolvedModel = "existing";

        session.UpdateResolvedModel(null);

        Assert.Equal("existing", session.ResolvedModel);
    }

    [Fact]
    public void ApplyUsage_AccumulatesTokenCounters()
    {
        var session = CreateSession();

        session.ApplyUsage(10, 5, 15, 2, 1, 0.001, "USD", 100, 200);
        session.ApplyUsage(20, 10, 30, 3, 2, 0.002, "USD", 150, 200);

        Assert.Equal(30, session.InputTokens);
        Assert.Equal(15, session.OutputTokens);
        Assert.Equal(45, session.TotalTokens);
        Assert.Equal(5, session.CachedReadTokens);
        Assert.Equal(3, session.ThoughtTokens);
    }

    [Fact]
    public void ApplyUsage_AccumulatesCostAndUpdatesCurrency()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, 0.001, "USD", null, null);
        session.ApplyUsage(null, null, null, null, null, 0.002, "EUR", null, null);

        Assert.Equal(0.003, session.CostAmount);
        Assert.Equal("EUR", session.CostCurrency);
    }

    [Fact]
    public void ApplyUsage_UpdatesContextWindowSnapshot()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, null, null, 100, 200);
        session.ApplyUsage(null, null, null, null, null, null, null, 150, 250);

        Assert.Equal(150, session.ContextWindowUsed);
        Assert.Equal(250, session.ContextWindowSize);
    }

    [Fact]
    public void ApplyUsage_NullDelta_DoesNotChangeExistingValues()
    {
        var session = CreateSession();
        session.InputTokens = 10;
        session.CostAmount = 0.005;
        session.ContextWindowUsed = 100;

        session.ApplyUsage(null, null, null, null, null, null, null, null, null);

        Assert.Equal(10, session.InputTokens);
        Assert.Equal(0.005, session.CostAmount);
        Assert.Equal(100, session.ContextWindowUsed);
    }

    [Fact]
    public void ApplyUsage_NegativeDelta_IgnoresDelta()
    {
        var session = CreateSession();
        session.InputTokens = 10;

        session.ApplyUsage(-5, -3, -8, null, null, -0.001, null, null, null);

        Assert.Equal(10, session.InputTokens);
        Assert.Null(session.OutputTokens);
        Assert.Null(session.TotalTokens);
        Assert.Null(session.CostAmount);
    }

    [Fact]
    public void ApplyUsage_TerminalSession_DoesNotMutate()
    {
        var session = CreateSession();
        session.Fail(DateTime.UtcNow, "error");

        session.ApplyUsage(10, 5, 15, null, null, 0.001, "USD", 100, 200);

        Assert.Null(session.InputTokens);
        Assert.Null(session.CostAmount);
    }

    [Fact]
    public void RecordToolCall_IncrementsToolCallCount()
    {
        var session = CreateSession();

        session.RecordToolCall(false);
        session.RecordToolCall(false);

        Assert.Equal(2, session.ToolCallCount);
        Assert.Null(session.ToolErrorCount);
    }

    [Fact]
    public void RecordToolCall_WithError_IncrementsBothCounters()
    {
        var session = CreateSession();

        session.RecordToolCall(true);

        Assert.Equal(1, session.ToolCallCount);
        Assert.Equal(1, session.ToolErrorCount);
    }

    [Fact]
    public void RecordToolCall_ErrorsDoNotExceedCalls()
    {
        var session = CreateSession();

        session.RecordToolCall(true);
        session.RecordToolCall(true);
        session.RecordToolCall(true);

        Assert.Equal(3, session.ToolCallCount);
        Assert.Equal(3, session.ToolErrorCount);
    }

    [Fact]
    public void RecordToolCall_TerminalSession_DoesNotMutate()
    {
        var session = CreateSession();
        session.Fail(DateTime.UtcNow, "error");

        session.RecordToolCall(false);

        Assert.Null(session.ToolCallCount);
        Assert.Null(session.ToolErrorCount);
    }

    [Fact]
    public void StartNewWork_ResetsCountersAndModelIndependence()
    {
        var session = CreateSession();
        session.Model = "intent";
        session.ResolvedModel = "resolved";
        session.InputTokens = 10;
        session.ToolCallCount = 5;
        session.ToolErrorCount = 1;
        session.FailureCategory = "probe_timeout";

        session.StartNewWork("runner-2", "work-2", "task", "Build", "Title", 1, DateTime.UtcNow);

        Assert.Equal("intent", session.Model);
        Assert.Equal("resolved", session.ResolvedModel);
        Assert.Equal(10, session.InputTokens);
        Assert.Equal(5, session.ToolCallCount);
        Assert.Equal(1, session.ToolErrorCount);
        Assert.Equal("probe_timeout", session.FailureCategory);
    }
}
