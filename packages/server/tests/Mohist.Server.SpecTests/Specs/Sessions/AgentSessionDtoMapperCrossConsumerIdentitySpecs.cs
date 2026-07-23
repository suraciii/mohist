using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Codifies the byte-alignment invariant that issue-370 T-001 pins between
/// the read-side consumers of <see cref="AgentSession"/> DTO projections.
/// These specs call consumer APIs where a consumer path exists and pin the
/// intentional current-list/activity-feed usage-history difference.
/// </summary>
[Collection("MohistDb")]
public class AgentSessionDtoMapperCrossConsumerIdentitySpecs
{
    private static readonly DateTime CreatedAt = new(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc);
    private readonly MohistDbFixture _fixture;

    public AgentSessionDtoMapperCrossConsumerIdentitySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UsageAndEventSummary_QuerierAndAssembler_PreserveAlignedFieldsAndHistoryDifference()
    {
        using var scope = _fixture.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var projectId = $"proj-cross-consumer-{Guid.NewGuid():N}";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var issueNumber = 731;

        await SeedIssueAsync(dbFactory, projectId, issueNumber, "Shared issue title", workflowRunId: null);
        await SeedGenericSessionWithUsageAndTranscriptAsync(dbFactory, projectId, sessionId, issueNumber);

        var sessionList = scope.ServiceProvider.GetRequiredService<AgentSessionListAssembler>();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var current = await sessionList.ListCurrentAsync(projectId, limit: 10);
        var activity = await assembler.GetActivityAsync(projectId, limit: 10);

        var fromQuerierPath = Assert.Single(current, session => session.SessionId == sessionId);
        var fromAssemblerPath = Assert.Single(activity.Sessions, session => session.SessionId == sessionId);

        Assert.Equal(fromQuerierPath.IssueNumber, fromAssemblerPath.IssueNumber);
        Assert.Equal(fromQuerierPath.IssueTitle, fromAssemblerPath.IssueTitle);
        AssertUsageDtoScalarFieldsEqual(fromQuerierPath.Usage, fromAssemblerPath.Usage);
        Assert.Null(fromQuerierPath.Usage.ContextUsageHistory);
        Assert.NotNull(fromAssemblerPath.Usage.ContextUsageHistory);
        AssertEventSummaryDtoEqual(fromQuerierPath.EventSummary, fromAssemblerPath.EventSummary);
        Assert.True(fromQuerierPath.EventSummary.ContextExhaustionSuspected);
        Assert.Null(fromQuerierPath.EventSummary.ContextExhaustion);
    }

    [Fact]
    public async Task Lineage_MetadataPath_ProducesSharedMapperProjection()
    {
        using var scope = _fixture.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
        var projectId = $"proj-lineage-{Guid.NewGuid():N}";
        var workflowRunId = $"wr-{Guid.NewGuid():N}";
        var sessionName = "plan";
        var issueNumber = 917;
        var session = CreateWorkflowSession(projectId, issueNumber, workflowRunId, sessionName);

        await SeedIssueAsync(dbFactory, projectId, issueNumber, "Workflow issue", workflowRunId);
        await SeedSessionAsync(dbFactory, session, rowStatus: "bound");

        var metadata = await querier.GetSessionMetadataAsync(projectId, issueNumber, sessionName);

        Assert.NotNull(metadata);
        var wire = JsonSerializer.SerializeToElement(metadata, JSON.Options);
        Assert.Equal("runtime-current", wire.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", wire.GetProperty("runtime").GetString());
        Assert.False(wire.TryGetProperty("runtimeSessionLineage", out _));
    }

    [Fact]
    public void Labels_SkipsBlankKeysAndValues_AndUsesOrdinalComparison()
    {
        var labels = AgentSessionDtoMapper.Labels(
            (null!, "ignored-null-key"),
            ("", "ignored-empty-key"),
            ("   ", "ignored-whitespace-key"),
            ("null-value", null),
            ("empty-value", ""),
            ("whitespace-value", "  "),
            ("Project", "upper"),
            ("project", "lower"));

        Assert.Equal(2, labels.Count);
        Assert.Equal("upper", labels["Project"]);
        Assert.Equal("lower", labels["project"]);
        Assert.False(labels.ContainsKey("null-value"));
        Assert.False(labels.ContainsKey("empty-value"));
        Assert.False(labels.ContainsKey("whitespace-value"));
    }

    private static async Task SeedGenericSessionWithUsageAndTranscriptAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string projectId,
        string sessionId,
        int issueNumber)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [GenericAgentSessionMetadata.AgentId] = "agent_cross_consumer",
            [GenericAgentSessionMetadata.AgentName] = "cross-consumer-agent",
        };

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("runner-cross", null),
            Settings = new AgentSessionSettings("gpt-4o"),
            Status = new AgentSessionStatusSnapshot(
                AgentRuntimeSessionId: sessionId,
                CreatedAt: CreatedAt,
                BoundAt: CreatedAt.AddSeconds(1),
                LastDataAt: CreatedAt.AddMinutes(5),
                UsageSummary: new AgentUsageSummary(
                    InputTokens: 100,
                    OutputTokens: 200,
                    TotalTokens: 300,
                    CachedReadTokens: 50,
                    ThoughtTokens: 25,
                    CostAmount: 0.42,
                    CostCurrency: "USD",
                    ContextWindowUsed: 60_000,
                    ContextWindowSize: 100_000),
                ContextUsageHistory:
                [
                    new ContextUsageHistoryEntry(CreatedAt.AddMinutes(1), 0.42),
                    new ContextUsageHistoryEntry(CreatedAt.AddMinutes(4), 0.60),
                ]),
            Metadata = new AgentSessionMetadata(labels),
        };

        await SeedSessionAsync(dbFactory, session, rowStatus: "bound");

        await using var db = await dbFactory.CreateDbContextAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddMinutes(5),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.AddRange(
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 1,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "model",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "gpt-4o" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(1),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 2,
                Type = TranscriptPartTypes.Tool,
                CorrelationKey = "tool-ok",
                PayloadJson = JsonSerializer.Serialize(new { toolCallId = "tool-ok", toolName = "read", status = "completed" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(2),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 3,
                Type = TranscriptPartTypes.Tool,
                CorrelationKey = "tool-failed",
                PayloadJson = JsonSerializer.Serialize(new { toolCallId = "tool-failed", toolName = "write", status = "failed" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(3),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 4,
                Type = TranscriptPartTypes.SessionActivity,
                CorrelationKey = "session.activity",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = ContextExhaustionClassifier.SuspectedContextExhaustionCategory }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(4),
            });
        await db.SaveChangesAsync();
    }

    private static AgentSession CreateWorkflowSession(
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.WorkId] = "work-lineage",
            [AgentSessionQueryMetadataKeys.WorkType] = "task",
            [AgentSessionQueryMetadataKeys.Stage] = "Build",
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [AgentSessionQueryMetadataKeys.SessionName] = sessionName,
        };

        return new AgentSession
        {
            Id = $"session-{Guid.NewGuid():N}",
            Runtime = new AgentSessionRuntime("runner-lineage", null, "opencode"),
            Settings = new AgentSessionSettings("gpt-4o"),
            Status = new AgentSessionStatusSnapshot(
                AgentRuntimeSessionId: "runtime-current",
                CreatedAt: CreatedAt,
                BoundAt: CreatedAt.AddMinutes(15),
                LastDataAt: CreatedAt.AddMinutes(20),
                UsageSummary: new AgentUsageSummary(),
                ContextUsageHistory: []),
            Metadata = new AgentSessionMetadata(labels),
        };
    }

    private static async Task SeedIssueAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string projectId,
        int number,
        string title,
        string? workflowRunId)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            WorkflowRunId = workflowRunId,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = IssueStatus.Backlog,
        };

        await using var db = await dbFactory.CreateDbContextAsync();
        db.Issues.Add(new IssueRow { ProjectId = projectId, Number = number, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSessionAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentSession session,
        string rowStatus)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = session.Status.CreatedAt,
            Status = rowStatus,
            AgentSessionId = session.Status.AgentRuntimeSessionId,
            RunnerId = session.Runtime.RunnerId,
        });
        await db.SaveChangesAsync();
    }

    private static void AssertUsageDtoScalarFieldsEqual(AgentUsageDto expected, AgentUsageDto actual)
    {
        Assert.Equal(expected.InputTokens, actual.InputTokens);
        Assert.Equal(expected.OutputTokens, actual.OutputTokens);
        Assert.Equal(expected.TotalTokens, actual.TotalTokens);
        Assert.Equal(expected.CachedReadTokens, actual.CachedReadTokens);
        Assert.Equal(expected.ThoughtTokens, actual.ThoughtTokens);
        Assert.Equal(expected.CostAmount, actual.CostAmount);
        Assert.Equal(expected.CostCurrency, actual.CostCurrency);
        Assert.Equal(expected.ContextWindowUsed, actual.ContextWindowUsed);
        Assert.Equal(expected.ContextWindowSize, actual.ContextWindowSize);
        Assert.Equal(expected.ContextUsagePercent, actual.ContextUsagePercent);
        Assert.Equal(expected.HealthStatus, actual.HealthStatus);
    }

    private static void AssertEventSummaryDtoEqual(AgentEventSummaryDto expected, AgentEventSummaryDto actual)
    {
        Assert.Equal(expected.ResolvedModel, actual.ResolvedModel);
        Assert.Equal(expected.FailureCategory, actual.FailureCategory);
        Assert.Equal(expected.ContextExhaustion, actual.ContextExhaustion);
        Assert.Equal(expected.ContextExhaustionSuspected, actual.ContextExhaustionSuspected);
        Assert.Equal(expected.ToolCallCount, actual.ToolCallCount);
        Assert.Equal(expected.ToolErrorCount, actual.ToolErrorCount);
    }

}
