using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class AgentSessionDomainSpecs
{
    private static AgentSession CreateSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel(AgentSessionMetadataKeys.ProjectId, "proj")
            .WithLabel(AgentSessionMetadataKeys.IssueNumber, "1")
            .WithLabel(AgentSessionMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionMetadataKeys.SourceId, "wf")
            .WithLabel(AgentSessionMetadataKeys.SessionName, "session")
            .WithAnnotation(AgentSessionMetadataKeys.TaskId, "work-1")
            .WithAnnotation(AgentSessionMetadataKeys.TaskKind, "task")
            .WithAnnotation(AgentSessionMetadataKeys.Phase, "Build")
            .WithAnnotation(AgentSessionMetadataKeys.Title, "Build work");

        return AgentSession.Create(
            "proj/wf/session",
            "runner-1",
            "opencode",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
    }

    private static AgentUsageSummary Usage(AgentSession session) => session.Status.UsageSummary ?? new AgentUsageSummary();

    [Fact]
    public void Create_OrganizesSessionIntoResourceSections()
    {
        var session = CreateSession();

        Assert.Equal("proj/wf/session", session.Id);
        Assert.Equal("proj", session.ProjectId);
        Assert.Equal("wf", session.RunId);
        Assert.Equal("session", session.SessionName);
        Assert.Equal(1, session.IssueNumber);
        Assert.Equal("work-1", session.TaskId);
        Assert.Equal("task", session.TaskKind);
        Assert.Equal("Build", session.Phase);
        Assert.Equal("Build work", session.Title);
        Assert.Equal("runner-1", session.Runtime.RunnerId);
        Assert.Equal("opencode", session.Runtime.AgentRuntime);
        Assert.Equal("/work", session.Runtime.WorkDir);
        Assert.Equal(AgentSessionStatus.Created, session.Status.Phase);
        Assert.Equal(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), session.Status.CreatedAt);
        Assert.NotNull(session.Status.UsageSummary);
    }

    [Fact]
    public void StateJson_UsesMetadataRuntimeSettingsAndStatusSections()
    {
        var session = CreateSession();

        session.AttachAgent("acp-1", "intent-model", "/work", "/change", 123, DateTime.UtcNow);
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

    [Fact]
    public void ApplyUsage_AccumulatesTokenCounters()
    {
        var session = CreateSession();

        session.ApplyUsage(10, 5, 15, 2, 1, 0.001, "USD", 100, 200);
        session.ApplyUsage(20, 10, 30, 3, 2, 0.002, "USD", 150, 200);

        Assert.Equal(30, Usage(session).InputTokens);
        Assert.Equal(15, Usage(session).OutputTokens);
        Assert.Equal(45, Usage(session).TotalTokens);
        Assert.Equal(5, Usage(session).CachedReadTokens);
        Assert.Equal(3, Usage(session).ThoughtTokens);
    }

    [Fact]
    public void ApplyUsage_AccumulatesCostAndUpdatesCurrency()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, 0.001, "USD", null, null);
        session.ApplyUsage(null, null, null, null, null, 0.002, "EUR", null, null);

        Assert.Equal(0.003, Usage(session).CostAmount);
        Assert.Equal("EUR", Usage(session).CostCurrency);
    }

    [Fact]
    public void ApplyUsage_UpdatesContextWindowSnapshot()
    {
        var session = CreateSession();

        session.ApplyUsage(null, null, null, null, null, null, null, 100, 200);
        session.ApplyUsage(null, null, null, null, null, null, null, 150, 250);

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

        session.ApplyUsage(null, null, null, null, null, null, null, null, null);

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

        session.ApplyUsage(-5, -3, -8, null, null, -0.001, null, null, null);

        Assert.Equal(10, Usage(session).InputTokens);
        Assert.Null(Usage(session).OutputTokens);
        Assert.Null(Usage(session).TotalTokens);
        Assert.Null(Usage(session).CostAmount);
    }

    [Fact]
    public void ApplyUsage_TerminalSession_DoesNotMutate()
    {
        var session = CreateSession();
        session.Fail(DateTime.UtcNow, "error");

        session.ApplyUsage(10, 5, 15, null, null, 0.001, "USD", 100, 200);

        Assert.Null(Usage(session).InputTokens);
        Assert.Null(Usage(session).CostAmount);
    }

    [Fact]
    public void StartNewWork_KeepsRuntimeAndUsageForSessionHistory()
    {
        var session = CreateSession();
        session.Settings = session.Settings with { Model = "intent" };
        session.Status = session.Status with { UsageSummary = Usage(session) with { InputTokens = 10 } };

        session.StartNewWork("runner-2", "work-2", "task", "Build", "Title", 1, DateTime.UtcNow);

        Assert.Equal("runner-1", session.Runtime.RunnerId);
        Assert.Equal("intent", session.Settings.Model);
        Assert.Equal(10, Usage(session).InputTokens);
    }
}
