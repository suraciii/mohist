using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("IntegrationWorkflow")]
public class WorkflowSessionSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"workflow-session-spec-runner-{Guid.NewGuid():N}";

    public WorkflowSessionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder()
    {
        var (project, issue, workflowRunId) = await CreateIssueWorkflowAsync("Runner reports ACP session events");
        var projectId = project.Id;
        var sessionName = "builder";

        var opened = await PostRawAsync<RunnerAgentSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/open", new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            title = "Generate proposal",
            issueNumber = issue.Number,
            runtime = "opencode",
        });
        await PostRawAsync<RunnerAgentSessionDto>(RunnerAgentSessionAttachPath("runner-1", projectId, workflowRunId, sessionName), new
        {
            runtimeSessionId = "runtime-1",
            workDir = "/workspace",
            model = "openai/gpt-4o",
            processPid = 123,
            runtime = "opencode",
            expectedRuntime = "opencode",
            expectedRuntimeSessionId = (string?)null,
        });
        var fetched = await GetRawAsync<RunnerAgentSessionDto>(RunnerSessionPath("runner-1", projectId, workflowRunId, sessionName));
        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);
        var persistence = _fixture.Persistence.Checkpoint(sessionId);

        await PostRawAsync<SessionEventDto[]>(RunnerSessionRuntimeEventsPath("runner-1", sessionId), new
        {
            runtimeSessionId = "runtime-1",
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "write proposal" } },
                new { type = "message.delta", payload = new { content = new { text = "done" } } },
                new { type = "usage.updated", payload = new { inputTokens = 10, outputTokens = 5, totalTokens = 15 } },
            },
        });
        await PostRawAsync<SessionEventDto[]>(RunnerSessionRuntimeEventsPath("runner-1", sessionId), new
        {
            runtimeSessionId = "runtime-1",
            runtimeEvents = new[]
            {
                new { type = "session.activity", payload = new { activity = "idle", status = "completed", exitCode = 0, operationId = "op-acp" } }
            },
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(sessionId, 3, persistence);

        var detail = await _client.GetDataAsync<WorkflowSessionDetailDto>($"/api/workflow-runs/{workflowRunId}/sessions/{sessionName}");
        var sessions = await _client.GetDataAsync<WorkflowSessionDto[]>($"/api/workflow-runs/{workflowRunId}/sessions");

        Assert.Equal(workflowRunId, opened.Key.WorkflowRunId);
        Assert.Equal(workflowRunId, fetched.Key.WorkflowRunId);
        Assert.Equal("runtime-1", fetched.RuntimeSessionId);
        Assert.Equal("opencode", fetched.Runtime);
        Assert.Equal("/workspace", fetched.WorkDir);
        Assert.Equal(sessionName, detail.Session.SessionName);
        Assert.Equal("runtime-1", detail.Session.RuntimeSessionId);
        Assert.Equal("opencode", detail.Session.Runtime);
        Assert.Equal("idle", detail.Session.Status);
        Assert.Equal("plan", detail.Session.Stage);
        Assert.Null(detail.Session.CompletedAt);
        Assert.Equal("openai/gpt-4o", detail.Session.Model);
        var listed = Assert.Single(sessions);
        Assert.Equal(sessionName, listed.SessionName);
        Assert.Equal("runtime-1", listed.RuntimeSessionId);
        Assert.Equal("opencode", listed.Runtime);
        Assert.Equal("idle", listed.Status);
        Assert.Equal("plan", listed.Stage);
        Assert.Null(listed.CompletedAt);
        Assert.NotNull(listed.Usage);
        Assert.Equal(15, listed.Usage!.TotalTokens);
        Assert.Equal(3, detail.Transcript.PartCount);
        var turn = Assert.Single(detail.Transcript.Turns);
        Assert.Equal("write proposal", turn.User.Text);
        Assert.Contains(turn.Assistant, p => p.Type == "text" && p.Text == "done");
        Assert.Null(turn.CompletedAt);
    }

    [Fact]
    public async Task GivenWorkflowSessionWithoutPhysicalBinding_WhenListed_ThenRuntimeSessionIdIsOmitted()
    {
        var (project, _, sessionName, workflowRunId) = await CreateIssueWorkflowSessionAsync("workflow-unbound-runtime");

        using var response = await _client.GetAsync($"/api/workflow-runs/{workflowRunId}/sessions");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var listed = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal(sessionName, listed.GetProperty("sessionName").GetString());
        Assert.False(listed.TryGetProperty("runtimeSessionId", out _));
    }

    [Fact]
    public async Task GivenMohistPromptAndTerminalFailure_WhenIssueWorkflowSessionEventsAreQueried_ThenRawEventsReturnInSequence()
    {
        const string promptBody =
            "Real full mohist_prompt text body. " +
            "It is longer than a short task title and is the exact text the agent sees. " +
            "Repeating the body to make sure the assertion fails on any truncation.";
        const string failureReason = "model refused to continue";
        var (project, issue, sessionName, workflowRunId) = await CreateIssueWorkflowSessionAsync("workflow-mohist-prompt");
        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);

        await _client.PostOkAsync(RunnerAgentSessionAttachPath(_runnerId, project.Id, workflowRunId, sessionName), new
        {
            runtimeSessionId = sessionId,
            runtime = "opencode",
            expectedRuntime = "opencode",
            expectedRuntimeSessionId = (string?)null,
            workDir = $"/workspaces/{project.Id}",
            processPid = 1234
        });
        var persistence = _fixture.Persistence.Checkpoint(sessionId);
        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(_runnerId, sessionId), new
        {
            runtimeSessionId = sessionId,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "session.input",
                    payload = new { text = promptBody, kind = "task" }
                },
                new { type = "message.delta", payload = new { text = "starting work" } },
                new
                {
                    type = "session.liveness",
                    payload = new { status = "probing", probeDeadlineAt = "2026-06-03T12:00:00Z", lastActivityType = "session" }
                },
                new
                {
                    type = "session.liveness",
                    payload = new { status = "failed", failureReason = "no progress", lastActivityType = "message" }
                },
                new
                {
                    type = "session.activity",
                    payload = new { activity = "idle", status = "failed", failureReason, exitCode = 1, operationId = "op-fail" }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(sessionId, 3, persistence);

        var metadata = await _client.GetDataAsync<IssueSessionMetadataTestDto>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}");
        Assert.Equal(sessionId, metadata.Id);
        Assert.Equal(sessionName, metadata.SessionName);
        Assert.Equal(3, metadata.Metadata.PartCount);
        Assert.Equal(0, metadata.Metadata.ToolCount);

        var transcript = await _client.GetDataAsync<IssueSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}/transcript");
        Assert.Equal(3, transcript.PartCount);
        var turn = Assert.Single(transcript.Turns);
        Assert.Equal(promptBody, turn.User.Text);
        Assert.Equal("task", turn.User.Kind);
        Assert.Contains(turn.Assistant, p => p.Type == "text" && p.Text == "starting work");
        Assert.Contains(turn.Assistant, p => p.Type == "error" && p.Kind == "failed" && p.Message == failureReason);

        var workflowSessions = await _client.GetDataAsync<WorkflowSessionDto[]>($"/api/workflow-runs/{workflowRunId}/sessions");
        var listed = Assert.Single(workflowSessions);
        // The activity model does not surface terminal session status; the
        // session's activity returns to idle and workflow-session fields that
        // previously mirrored the close payload (completedAt / failureReason /
        // exitCode) are no longer projected here. The failure fact is still
        // observable in the transcript error part asserted above.
        Assert.Equal("idle", listed.Status);
        Assert.Equal("Build", listed.Stage);
        Assert.Null(listed.CompletedAt);
        Assert.Null(listed.FailureReason);
        Assert.Null(listed.ExitCode);
    }

    [Fact]
    public async Task GivenOpenRequestOmitsRuntime_ThenServerRejectsWithRuntimeInvalid()
    {
        var (project, _, workflowRunId) = await CreateIssueWorkflowAsync("Open without runtime");
        var sessionName = $"open-no-runtime-{Guid.NewGuid():N}";

        var path = RunnerSessionPath("runner-1", project.Id, workflowRunId, sessionName) + "/open";
        using var response = await _client.PostAsJsonAsync(path, new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            title = "Generate proposal",
            runtime = (string?)null,
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("runtime_invalid", body!.RootElement.GetProperty("code").GetString());
    }

    private async Task<(ProjectDto Project, IssueDto Issue, string SessionName, string WorkflowRunId)> CreateIssueWorkflowSessionAsync(string name, string? title = null)
    {
        var issueTitle = title ?? $"Workflow session {name}";
        var (project, issue, workflowRunId) = await CreateIssueWorkflowAsync(issueTitle);
        var sessionName = $"task-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid().ToString("N");
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .OpenAsync(new OpenAgentSessionCommand(
                _runnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(project.Id, issue.Number, workflowRunId, sessionName, sessionName, "task", "Build", issueTitle)));

        return (project, issue, sessionName, workflowRunId);
    }

    private async Task<(ProjectDto Project, IssueDto Issue, string WorkflowRunId)> CreateIssueWorkflowAsync(string title)
    {
        var projectName = $"wfs-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = "https://example.com/repo.git",
            baseBranch = "main",
            setDefault = true
        });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title,
            body = "track workflow session",
            labels = new Dictionary<string, string>(StringComparer.Ordinal),
            priority = "p1",
            isDraft = false
        });

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();
        await DispatchEventsAsync();
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        return (project, issue, workflowRunId);
    }

    private Task DispatchEventsAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task<T> PostRawAsync<T>(string path, object body)
    {
        using var response = await _client.PostAsJsonAsync(path, body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
    }

    private async Task<T> GetRawAsync<T>(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
    }

    private static string RunnerAgentSessionAttachPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"{RunnerSessionPath(runnerId, projectId, workflowRunId, sessionName)}/attach";

    private static string RunnerSessionRuntimeEventsPath(string runnerId, string sessionId) =>
        $"/api/runner/{runnerId}/agent-sessions/{sessionId}/runtime-events";

    private static string RunnerSessionPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}";

    private Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName) =>
        WorkflowApiTestSupport.ResolveSessionIdAsync(_fixture.Services, workflowRunId, sessionName);

    private static AgentSessionMetadata WorkflowSessionMetadata(
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string? workId,
        string? workType,
        string? stage,
        string? title) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
            .WithLabel(AgentSessionQueryMetadataKeys.SessionName, sessionName)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkId, workId)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkType, workType)
            .WithLabel(AgentSessionQueryMetadataKeys.Stage, stage)
            .WithAnnotation(AgentSessionQueryMetadataKeys.Title, title);

    private sealed record RunnerAgentSessionDto(RunnerAgentSessionKeyDto Key, string? RuntimeSessionId, string Status, string? WorkDir, string? Model, string? Runtime);
    private sealed record RunnerAgentSessionKeyDto(string ProjectId, string WorkflowRunId, string SessionName);
    private sealed record WorkflowSessionDto(string Id, string WorkflowRunId, string SessionName, string? RuntimeSessionId, string? Runtime, [property: System.Text.Json.Serialization.JsonPropertyName("activity")] string Status, string? Stage, string? Model, string? CompletedAt, string? FailureReason, int? ExitCode, WorkflowSessionUsageDto? Usage);
    private sealed record WorkflowSessionUsageDto(long? TotalTokens);
    private sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, IssueSessionTranscriptTestResponse Transcript);
    private sealed record SessionEventDto(long Sequence, string Type, string? WorkId);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(
        string Id,
        int Number,
        string Title,
        string Status,
        string? WorkflowRunId,
        string? WorkflowProfileId = null);
    private sealed record IssueWorkflowProfileDto(string? ProfileId);
    private sealed record IssueSessionMetadataTestDto(string Id, string SessionName, IssueSessionMetadataCountsTestDto Metadata);
    private sealed record IssueSessionMetadataCountsTestDto(int PartCount, int ToolCount);
    private sealed record IssueSessionTranscriptTestResponse(IssueSessionTranscriptTurnTestDto[] Turns, int PartCount, string? LastActivityAt);
    private sealed record IssueSessionTranscriptTurnTestDto(string Id, string StartedAt, string? CompletedAt, bool Incomplete, IssueSessionTranscriptUserTestDto User, IssueSessionTranscriptPartTestDto[] Assistant);
    private sealed record IssueSessionTranscriptUserTestDto(string Text, string Kind, string SentAt);
    private sealed record IssueSessionTranscriptPartTestDto(string Id, string Type, string? Text, string? ToolCallId, string? Status, string? StartedAt, string? CompletedAt, string? Message, string? Kind, string? At);
}
