using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class SessionFollowupApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"followup-api-{Guid.NewGuid():N}";

    public SessionFollowupApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task FollowupEndpoint_ActiveSessionOnlineRunner_ReturnsSent()
    {
        var (project, issue, workflowRunId, session) = await CreateAndStartSessionAsync("followup-ok", sessionName: "plan", attachAndStart: true);
        var sessionState = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.Equal("active", sessionState?.Status);
        var tasksBefore = await GetWorkflowTaskSnapshotAsync(project.Id, issue.Number);
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-followup-1");
        try
        {
            using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "加个登出" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(session.Id, data.GetProperty("sessionId").GetString());
            Assert.Equal("sent", data.GetProperty("status").GetString());

            var sent = Assert.Single(runnerHub.SentMessages);
            Assert.Equal("conn-followup-1", sent.ConnectionId);
            Assert.Equal("ReceiveFollowup", sent.Method);
            var payload = JsonSerializer.SerializeToElement(sent.Arguments.Single());
            Assert.Equal(workflowRunId, payload.GetProperty("workflowRunId").GetString());
            Assert.Equal("plan", payload.GetProperty("sessionName").GetString());
            Assert.Equal("加个登出", payload.GetProperty("text").GetString());

            var tasksAfter = await GetWorkflowTaskSnapshotAsync(project.Id, issue.Number);
            Assert.Equal(tasksBefore, tasksAfter);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task FollowupEndpoint_IdleLiveSession_StartsUserTurnWithoutCreatingTask()
    {
        var (project, issue, _, session) = await CreateAndStartSessionAsync("followup-idle", sessionName: "plan", attachAndStart: true);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        Assert.NotEqual("active", (await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync())?.Status);
        var tasksBefore = await GetWorkflowTaskSnapshotAsync(project.Id, issue.Number);

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-followup-idle");
        try
        {
            using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "start an idle turn" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ReceiveFollowup", Assert.Single(runnerHub.SentMessages).Method);
            Assert.Equal(tasksBefore, await GetWorkflowTaskSnapshotAsync(project.Id, issue.Number));
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task FollowupEndpoint_EmptyText_ReturnsBadRequest()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("followup-empty", sessionName: "plan");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_WhitespaceText_ReturnsBadRequest()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("followup-whitespace", sessionName: "plan");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "   \t  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_MissingText_ReturnsBadRequest()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("followup-missing", sessionName: "plan");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_UnknownSession_ReturnsNotFound()
    {
        var (project, issue) = await CreateProjectAndIssueAsync("followup-not-found");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "does-not-exist", new { text = "ping" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FollowupEndpoint_MissingRuntimeBinding_ReturnsRuntimeSessionMissing()
    {
        var (project, issue, _, session) = await CreateAndStartSessionAsync("followup-missing-runtime", sessionName: "plan");
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "ping" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime_session_missing", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(session.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        Assert.Equal("reset", doc.RootElement.GetProperty("details").GetProperty("hint").GetString());
        Assert.Contains(session.Id, doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("Reset", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(runnerHub.SentMessages);
    }

    [Fact]
    public async Task FollowupEndpoint_RunnerOffline_ReturnsServiceUnavailable()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("followup-offline", sessionName: "plan", attachAndStart: true);

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "ping" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runner_offline", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResolveFollowupTargetAsync_ReadsRunnerIdAndWorkflowRunIdFromSession()
    {
        var (project, issue, workflowRunId, _) = await CreateAndStartSessionAsync("followup-target", sessionName: "plan");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<Mohist.Server.Sessions.Services.AgentSessionQuerier>();

        var target = await querier.ResolveFollowupTargetAsync(project.Id, issue.Number, "plan");

        Assert.NotNull(target);
        Assert.Equal(_runnerId, target!.RunnerId);
        Assert.Equal(workflowRunId, target.WorkflowRunId);
        Assert.Equal("plan", target.SessionName);
        Assert.False(target.IsActive);
    }

    private Task<HttpResponseMessage> PostFollowupAsync(string projectId, int issueNumber, string sessionName, object body) =>
        _client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueNumber}/sessions/{sessionName}/followup", body);

    private async Task<string[]> GetWorkflowTaskSnapshotAsync(string projectId, int issueNumber)
    {
        var status = await _fixture.Grains
            .GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .GetWorkflowStatusAsync();
        return status?.Workflow?.Stages
            .SelectMany(stage => stage.Tasks)
            .Select(task => $"{task.Id}:{task.Status}")
            .ToArray() ?? [];
    }

    private async Task<(ProjectDto Project, IssueDto Issue, string WorkflowRunId, CreatedSession Session)> CreateAndStartSessionAsync(
        string name,
        string sessionName = "plan",
        bool attachAndStart = false)
    {
        var (project, issue) = await CreateProjectAndIssueAsync(name);
        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: $"Session followup {name}",
            Issue: new WorkIssueRef(project.Id, issue.Number));

        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], "followup-host", project.Id));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();
        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, sessionName, work, $"Session followup {name}");

        if (attachAndStart)
        {
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        }

        return (project, issue, currentWorkflowRunId, currentSession);
    }

    private async Task<(ProjectDto Project, IssueDto Issue)> CreateProjectAndIssueAsync(string name)
    {
        var projectName = $"followup-api-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = $"Followup api {name}", body = "followup sessions", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        return (project, issue);
    }

    private async Task<CreatedSession> OpenRunnerSessionAsync(string projectId, int issueNumber, string workflowRunId, string sessionName, WorkDispatch work, string title)
    {
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title,
            issueNumber
        });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);
        var session = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        return new CreatedSession(projectId, issueNumber, workflowRunId, sessionName, session ?? throw new InvalidOperationException($"Session {workflowRunId}/{sessionName} was not created."));
    }

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        // AgentSessionLabels index table was replaced by STORED computed
        // columns on AgentSessions (LabelSourceId / LabelSessionName, ...),
        // matching the production query in AgentSessionQuery.
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}/attach";

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(int Number, string Title);
    private sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }
}
