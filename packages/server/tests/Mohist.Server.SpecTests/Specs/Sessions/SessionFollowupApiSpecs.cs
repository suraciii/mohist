using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("RunnerMutationIntegration")]
public class SessionFollowupApiSpecs : IAsyncDisposable
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"followup-api-{Guid.NewGuid():N}";

    public SessionFollowupApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // The runner is registered into the collection-wide RunnerRegistryGrain
    // (a single global grain shared by every class in IntegrationSessions).
    // Leaving it registered leaks a live, idle runner that competes for
    // AgentJob dispatch in later classes: ListEligibleRunnersAsync ignores
    // project affinity and returns every registered runner, so a leaked
    // followup-api runner can be assigned a later test's agent-job launch
    // (the work then never reaches that test's own runner and its poll
    // times out). Unregister on teardown so the registry reflects only
    // the currently-running test's runners.
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", content: null);
            _ = response;
        }
        catch
        {
        }
    }

    [Fact]
    public async Task FollowupEndpoint_ActiveSessionOnlineRunner_ReturnsAccepted()
    {
        var (project, issue, workflowRunId, session) = await CreateAndStartSessionAsync("followup-ok", sessionName: "plan", attachAndStart: true);
        var sessionState = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.Equal("idle", sessionState?.Status);
        var tasksBefore = await WaitForWorkflowTasksAsync(project.Id, issue.Number);
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
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
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("inputId").GetString()));
            Assert.False(string.IsNullOrEmpty(data.GetProperty("turnId").GetString()));

            var sent = Assert.Single(runnerHub.SentMessages);
            Assert.Equal(_runnerId, sent.ConnectionId);
            Assert.Equal("session.followup", sent.Method);
            var payload = JsonSerializer.SerializeToElement(sent.Arguments.Single());
            var wireTarget = payload.GetProperty("target");
            Assert.Equal(workflowRunId, wireTarget.GetProperty("workflowRunId").GetString());
            Assert.Equal("plan", wireTarget.GetProperty("sessionName").GetString());
            Assert.Equal(session.Id, wireTarget.GetProperty("sessionId").GetString());
            Assert.Equal("加个登出", payload.GetProperty("text").GetString());
            Assert.Equal(data.GetProperty("inputId").GetString(), payload.GetProperty("inputId").GetString());
            Assert.Equal(data.GetProperty("turnId").GetString(), payload.GetProperty("turnId").GetString());

            var tasksAfter = await GetWorkflowTaskSnapshotAsync(project.Id, issue.Number);
            Assert.Equal(tasksBefore, tasksAfter);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_WhitespaceText_ReturnsBadRequest()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("followup-whitespace", sessionName: "plan");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "   \t  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_MissingText_ReturnsBadRequest()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("followup-missing", sessionName: "plan");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_UnknownSession_ReturnsNotFound()
    {
        var (project, issue) = await CreateProjectAndIssueAsync("followup-not-found");

        using var response = await PostFollowupAsync(project.Id, issue.Number, "does-not-exist", new { text = "ping" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var notFoundDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", notFoundDoc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task FollowupEndpoint_MissingRuntimeBinding_ReturnsRuntimeSessionMissing()
    {
        var (project, issue, _, session) = await CreateAndStartSessionAsync("followup-missing-runtime", sessionName: "plan");
        var runnerHub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();

        using var response = await PostFollowupAsync(project.Id, issue.Number, "plan", new { text = "ping" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("rejected", data.GetProperty("status").GetString());
            Assert.Equal("runtime_session_missing", data.GetProperty("code").GetString());
            Assert.Equal(session.Id, data.GetProperty("sessionId").GetString());
            Assert.Empty(runnerHub.SentMessages);
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

    private async Task<string[]> WaitForWorkflowTasksAsync(string projectId, int issueNumber)
    {
        return await TestWait.ForAsync(
            () => GetWorkflowTaskSnapshotAsync(projectId, issueNumber),
            snapshot => snapshot.Length > 0,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            "workflow tasks populated",
            advance: AdvanceClusterTurnAsync);
    }

    private async Task AdvanceClusterTurnAsync()
    {
        await _fixture.Grains
            .GetGrain<IRunnerRegistryGrain>(Mohist.Server.Runner.Grains.RunnerRegistryKeys.Global)
            .ListRunnerIdsAsync();
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
            Uses: "mohist/opencode",
            WorkType: "task",
            Stage: "Build",
            Title: $"Session followup {name}",
            Issue: new WorkIssueRef(project.Id, issue.Number));

        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).RegisterAsync(new RunnerInfo(
            _runnerId,
            ["spec/*"],
            "followup-host",
            project.Id,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        var wrId = await issueGrain.StartWorkAsync();
        await _fixture.Grains.GetGrain<IWorkflowGrain>(wrId).EnsureStartedAsync(new WorkflowIssueContext(project.Id, issue.Number, null));
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
            issueNumber,
            runtime = "opencode"
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
