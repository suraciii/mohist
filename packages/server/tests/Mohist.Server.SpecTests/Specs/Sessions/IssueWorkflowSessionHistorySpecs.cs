using System.Net;
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

[Collection("IntegrationSessions")]
public class IssueWorkflowSessionHistorySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueWorkflowSessionHistorySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(IssueStatus.Done)]
    [InlineData(IssueStatus.Cancelled)]
    public async Task ReadRoutes_ReturnHistoricalSessionForTerminalIssue(IssueStatus status)
    {
        var projectId = await CreateProjectAsync($"history-{status}");
        const int issueNumber = 459;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, status, workflowRunId: null);
        var sessionId = await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-history", sessionName, TestTime.UtcDateTime, "historical answer");

        var metadata = await _fixture.Client.GetDataAsync<JsonElement>(SessionPath(projectId, issueNumber, sessionName));
        var transcript = await _fixture.Client.GetDataAsync<JsonElement>($"{SessionPath(projectId, issueNumber, sessionName)}/transcript");

        Assert.Equal(sessionId, metadata.GetProperty("id").GetString());
        Assert.Equal("historical answer", transcript.GetProperty("turns")[0].GetProperty("assistant")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadRoutes_SelectNewestMatchingHistoricalSession()
    {
        var projectId = await CreateProjectAsync("history-newest");
        const int issueNumber = 460;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-old", sessionName, TestTime.UtcDateTime.AddHours(-2), "old answer");
        var newestId = await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-new", sessionName, TestTime.UtcDateTime.AddHours(-1), "new answer");

        var metadata = await _fixture.Client.GetDataAsync<JsonElement>(SessionPath(projectId, issueNumber, sessionName));
        var transcript = await _fixture.Client.GetDataAsync<JsonElement>($"{SessionPath(projectId, issueNumber, sessionName)}/transcript");

        Assert.Equal(newestId, metadata.GetProperty("id").GetString());
        Assert.Equal("new answer", transcript.GetProperty("turns")[0].GetProperty("assistant")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadRoutes_ActiveRunTakesPrecedenceAndDoesNotFallback()
    {
        var projectId = await CreateProjectAsync("history-active");
        const int issueNumber = 461;
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.InProgress, workflowRunId: "wr-active");
        var activeId = await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-active", "plan", TestTime.UtcDateTime, "active answer");
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-earlier", "history-only", TestTime.UtcDateTime.AddHours(-1), "earlier answer");

        var active = await _fixture.Client.GetDataAsync<JsonElement>(SessionPath(projectId, issueNumber, "plan"));
        Assert.Equal(activeId, active.GetProperty("id").GetString());

        using var missing = await _fixture.Client.GetAsync(SessionPath(projectId, issueNumber, "history-only"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ReadRoutes_RequireIssueProjectAndWorkflowSourceBoundaries()
    {
        var projectId = await CreateProjectAsync("history-boundaries");
        var otherProjectId = await CreateProjectAsync("history-other-project");
        const int issueNumber = 462;
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);
        await SeedWorkflowSessionAsync(otherProjectId, issueNumber, "wr-other-project", "plan", TestTime.UtcDateTime, "wrong project");
        await SeedWorkflowSessionAsync(projectId, issueNumber + 1, "wr-other-issue", "plan", TestTime.UtcDateTime, "wrong issue");
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-generic", "plan", TestTime.UtcDateTime.AddMinutes(1), "wrong source", sourceKind: "agent-launch");

        using var response = await _fixture.Client.GetAsync(SessionPath(projectId, issueNumber, "plan"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transcript_HistoricalReadFiltersByRuntimeSessionId()
    {
        var projectId = await CreateProjectAsync("history-runtime-filter");
        const int issueNumber = 463;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Cancelled, workflowRunId: null);
        var sessionId = await SeedWorkflowSessionAsync(
            projectId,
            issueNumber,
            "wr-history-runtime",
            sessionName,
            TestTime.UtcDateTime,
            "first answer",
            runtimeSessionIds: ["runtime-first", "runtime-second"],
            assistantTexts: ["first answer", "second answer"]);

        var transcript = await _fixture.Client.GetDataAsync<JsonElement>(
            $"{SessionPath(projectId, issueNumber, sessionName)}/transcript?runtimeSessionId=runtime-second");

        Assert.Equal(1, transcript.GetProperty("turns").GetArrayLength());
        Assert.Equal("runtime-second", transcript.GetProperty("turns")[0].GetProperty("user").GetProperty("runtimeSessionId").GetString());
        Assert.Equal("second answer", transcript.GetProperty("turns")[0].GetProperty("assistant")[0].GetProperty("text").GetString());
        Assert.Equal(sessionId, (await _fixture.Client.GetDataAsync<JsonElement>(SessionPath(projectId, issueNumber, sessionName))).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Commands_DoNotResolveHistoricalIssueSession()
    {
        var projectId = await CreateProjectAsync("history-commands");
        const int issueNumber = 464;
        const string sessionName = "plan";
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);
        await SeedWorkflowSessionAsync(projectId, issueNumber, "wr-history-commands", sessionName, TestTime.UtcDateTime, "historical answer");

        var basePath = SessionPath(projectId, issueNumber, sessionName);
        using var followup = await _fixture.Client.PostAsJsonAsync($"{basePath}/followup", new { text = "continue" });
        using var compact = await _fixture.Client.PostAsync($"{basePath}/compact", content: null);
        using var reset = await _fixture.Client.PostAsync($"{basePath}/reset", content: null);
        using var cancel = await _fixture.Client.PostAsync($"{basePath}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, followup.StatusCode);
        using var followupBody = JsonDocument.Parse(await followup.Content.ReadAsStringAsync());
        Assert.Equal("rejected", followupBody.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, compact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Fact]
    public async Task ReadRoutes_ReturnNotFoundWhenNoMatchingSessionExists()
    {
        var projectId = await CreateProjectAsync("history-missing");
        const int issueNumber = 465;
        await SeedIssueAsync(projectId, issueNumber, IssueStatus.Done, workflowRunId: null);

        using var metadata = await _fixture.Client.GetAsync(SessionPath(projectId, issueNumber, "missing"));
        using var transcript = await _fixture.Client.GetAsync($"{SessionPath(projectId, issueNumber, "missing")}/transcript");

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
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

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private static string SessionPath(string projectId, int issueNumber, string sessionName) =>
        $"/api/projects/{projectId}/issues/{issueNumber}/sessions/{sessionName}";
}
