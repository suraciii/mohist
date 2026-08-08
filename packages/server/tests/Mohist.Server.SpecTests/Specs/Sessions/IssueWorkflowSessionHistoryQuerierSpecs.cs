using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using IssueEntity = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Calculation specs for the historical workflow-session lookup behind
/// <c>GET /api/projects/{projectRef}/issues/{number}/sessions/{name}</c>
/// and its transcript projection. The querier resolves by
/// (project, issue, workflow-run, session-name) when the issue still
/// pins a workflow-run, falling back to the most recent
/// source-kind=workflow session for the issue when the workflow-run
/// label has been cleared by issue completion. Specs drive the same
/// <see cref="AgentSessionQuerier.GetSessionMetadataAsync"/> /
/// <see cref="AgentSessionQuerier.GetSessionTranscriptAsync"/> directly
/// via <c>MohistDbFixture</c> (no web host, no HTTP). The route contract
/// (404 unknown issue, 404 no matching session, project/source boundary
/// 404) stays in <c>IssueWorkflowSessionHistorySpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueWorkflowSessionHistoryQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueWorkflowSessionHistoryQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(IssueStatus.Done)]
    [InlineData(IssueStatus.Cancelled)]
    public async Task GetSessionMetadataAsync_ReturnsHistoricalSessionForTerminalIssue(IssueStatus status)
    {
        var projectId = $"proj-history-{status}-{Guid.NewGuid():N}";
        const int issueNumber = 459;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, status, workflowRunId: null);
        var sessionId = await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-history", sessionName, TestTime.UtcDateTime, "historical answer");

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var metadata = await querier.GetSessionMetadataAsync(projectId, issueNumber, sessionName);

        Assert.NotNull(metadata);
        Assert.Equal(sessionId, metadata!.Id);
    }

    [Fact]
    public async Task GetSessionTranscriptAsync_ReturnsHistoricalTranscriptForTerminalIssue()
    {
        var projectId = $"proj-history-tx-{Guid.NewGuid():N}";
        const int issueNumber = 4595;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-history-tx", sessionName, TestTime.UtcDateTime, "historical answer");

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var transcript = await querier.GetSessionTranscriptAsync(projectId, issueNumber, sessionName);

        Assert.NotNull(transcript);
        Assert.Single(transcript!.Turns);
        Assert.Equal("historical answer", transcript.Turns[0].Assistant[0].Text);
    }

    [Fact]
    public async Task GetSessionMetadataAsync_SelectsNewestMatchingHistoricalSession()
    {
        var projectId = $"proj-history-newest-{Guid.NewGuid():N}";
        const int issueNumber = 460;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-old", sessionName, TestTime.UtcDateTime.AddHours(-2), "old answer");
        var newestId = await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-new", sessionName, TestTime.UtcDateTime.AddHours(-1), "new answer");

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var metadata = await querier.GetSessionMetadataAsync(projectId, issueNumber, sessionName);

        Assert.NotNull(metadata);
        Assert.Equal(newestId, metadata!.Id);
    }

    [Fact]
    public async Task GetSessionMetadataAsync_ActiveRunTakesPrecedenceAndDoesNotFallback()
    {
        var projectId = $"proj-history-active-{Guid.NewGuid():N}";
        const int issueNumber = 461;
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.InProgress, workflowRunId: "wr-active");
        var activeId = await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-active", "plan", TestTime.UtcDateTime, "active answer");
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-earlier", "history-only", TestTime.UtcDateTime.AddHours(-1), "earlier answer");

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var active = await querier.GetSessionMetadataAsync(projectId, issueNumber, "plan");
        var missing = await querier.GetSessionMetadataAsync(projectId, issueNumber, "history-only");

        Assert.NotNull(active);
        Assert.Equal(activeId, active!.Id);
        Assert.Null(missing);
    }

    [Fact]
    public async Task GetSessionTranscriptAsync_HistoricalReadFiltersByRuntimeSessionId()
    {
        var projectId = $"proj-history-runtime-{Guid.NewGuid():N}";
        const int issueNumber = 463;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Cancelled, workflowRunId: null);
        await SeedWorkflowSessionAsync(
            projectId,
            issueNumber,
            "wr-history-runtime",
            sessionName,
            TestTime.UtcDateTime,
            "first answer",
            runtimeSessionIds: ["runtime-first", "runtime-second"],
            assistantTexts: ["first answer", "second answer"]);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var transcript = await querier.GetSessionTranscriptAsync(
            projectId, issueNumber, sessionName, runtimeSessionId: "runtime-second");

        Assert.NotNull(transcript);
        Assert.Single(transcript!.Turns);
        Assert.Equal("runtime-second", transcript.Turns[0].User.RuntimeSessionId);
        Assert.Equal("second answer", transcript.Turns[0].Assistant[0].Text);
    }

    [Fact]
    public async Task GetSessionMetadataAsync_RequiresWorkflowSourceKind_ForHistoricalFallback()
    {
        var projectId = $"proj-history-source-{Guid.NewGuid():N}";
        const int issueNumber = 4630;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-generic", sessionName, TestTime.UtcDateTime.AddMinutes(1), "wrong source", sourceKind: "agent-launch");

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var metadata = await querier.GetSessionMetadataAsync(projectId, issueNumber, sessionName);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task GetSessionMetadataAsync_ReturnsNullWhenNoMatchingSessionExists()
    {
        var projectId = $"proj-history-missing-{Guid.NewGuid():N}";
        const int issueNumber = 465;
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var metadata = await querier.GetSessionMetadataAsync(projectId, issueNumber, "missing");
        var transcript = await querier.GetSessionTranscriptAsync(projectId, issueNumber, "missing");

        Assert.Null(metadata);
        Assert.Null(transcript);
    }

    private async Task<string> SeedWorkflowSessionAsync(
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        DateTime createdAt,
        string assistantText,
        string sourceKind = "workflow",
        IReadOnlyList<string>? runtimeSessionIds = null,
        IReadOnlyList<string>? assistantTexts = null)
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var runtimes = runtimeSessionIds ?? [sessionId];
        var texts = assistantTexts ?? [assistantText];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [AgentSessionQueryMetadataKeys.SourceKind] = sourceKind,
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.SessionName] = sessionName,
            [AgentSessionQueryMetadataKeys.WorkId] = $"work-{sessionId}",
            [AgentSessionQueryMetadataKeys.WorkType] = "task",
            [AgentSessionQueryMetadataKeys.Stage] = "Build",
        };
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null, "opencode"),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(AgentRuntimeSessionId: runtimes[^1], CreatedAt: createdAt),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = "closed",
            AgentSessionId = runtimes[^1],
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();

        for (var index = 0; index < runtimes.Count; index++)
        {
            var turn = new AgentSessionTranscriptTurnRow
            {
                SessionId = sessionId,
                RuntimeSessionId = runtimes[index],
                Sequence = index + 1,
                PromptText = $"prompt {index + 1}",
                PromptKind = "task",
                StartedAt = createdAt.AddMinutes(index),
                UpdatedAt = createdAt.AddMinutes(index),
            };
            db.AgentSessionTranscriptTurns.Add(turn);
            await db.SaveChangesAsync();
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 1,
                Type = "text",
                Text = texts[index],
                CorrelationKey = $"text-{index}",
                PayloadJson = "{}",
                FirstSeenAt = createdAt.AddMinutes(index),
                LastSeenAt = createdAt.AddMinutes(index),
            });
            await db.SaveChangesAsync();
        }

        return sessionId;
    }

    private async Task SeedIssueAsync(string projectId, int issueNumber, IssueStatus status, string? workflowRunId)
    {
        var issue = new IssueEntity
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            WorkflowRunId = workflowRunId,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }
}