using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

[Collection("MohistIntegration")]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var sessionName = "builder";

        var opened = await PostRawAsync<RunnerAgentSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/open", new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            title = "Generate proposal",
            issueNumber = 7,
        });
        await PostRawAsync<RunnerAgentSessionDto>(RunnerAgentSessionAttachPath("runner-1", projectId, workflowRunId, sessionName), new
        {
            agentSessionId = "acp-1",
            workDir = "/workspace",
            model = "openai/gpt-4o",
            processPid = 123,
        });

        await PostRawAsync<SessionEventDto[]>(RunnerAgentSessionRuntimeEventsPath("runner-1", projectId, workflowRunId, sessionName), new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "write proposal" } },
                new { type = "message.delta", payload = new { content = new { text = "done" } } },
            },
        });
        await PostRawAsync<SessionEventDto[]>(RunnerAgentSessionRuntimeEventsPath("runner-1", projectId, workflowRunId, sessionName), new
        {
            runtimeEvents = new object[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            },
        });

        var detail = await _client.GetDataAsync<WorkflowSessionDetailDto>($"/api/workflow-runs/{workflowRunId}/sessions/{sessionName}");

        Assert.Equal(workflowRunId, opened.Key.WorkflowRunId);
        Assert.Equal(sessionName, detail.Session.SessionName);
        Assert.Equal("acp-1", detail.Session.AgentSessionId);
        Assert.Equal("active", detail.Session.Status);
        Assert.Equal("openai/gpt-4o", detail.Session.Model);
        Assert.Equal(3, detail.Transcript.SegmentCount);
        var turn = Assert.Single(detail.Transcript.Turns);
        Assert.Equal("write proposal", turn.User.Text);
        Assert.Contains(turn.Assistant, p => p.Type == "text" && p.Text == "done");
        Assert.NotNull(turn.CompletedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
            agentSessionId = sessionId,
            workDir = project.Path,
            processPid = 1234
        });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(_runnerId, project.Id, workflowRunId, sessionName), new
        {
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
                    type = "session.closed",
                    payload = new { status = "failed", failureReason, exitCode = 1 }
                }
            }
        });

        var metadata = await _client.GetDataAsync<IssueSessionMetadataTestDto>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}");
        Assert.Equal(sessionId, metadata.Id);
        Assert.Equal(sessionName, metadata.SessionName);
        Assert.Equal(5, metadata.Metadata.SegmentCount);
        Assert.Equal(0, metadata.Metadata.ToolCount);

        var transcript = await _client.GetDataAsync<IssueSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}/transcript");
        Assert.Equal(5, transcript.SegmentCount);
        var turn = Assert.Single(transcript.Turns);
        Assert.Equal(promptBody, turn.User.Text);
        Assert.Equal("task", turn.User.Kind);
        Assert.Contains(turn.Assistant, p => p.Type == "text" && p.Text == "starting work");
        Assert.Contains(turn.Assistant, p => p.Type == "error" && p.Kind == "failed" && p.Message == failureReason);
    }

    private async Task<(ProjectDto Project, IssueDto Issue, string SessionName, string WorkflowRunId)> CreateIssueWorkflowSessionAsync(string name, string? title = null)
    {
        var projectName = $"wfs-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new
        {
            name = projectName,
            path = Directory.GetCurrentDirectory(),
            baseBranch = "main"
        });
        var issueTitle = title ?? $"Workflow session {name}";
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = issueTitle,
            body = "track workflow session",
            labels = Array.Empty<string>(),
            priority = "p1"
        });

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var sessionName = $"task-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid().ToString("N");
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .OpenAsync(new OpenAgentSessionCommand(
                project.Id, issue.Number, workflowRunId, sessionName, _runnerId,
                sessionName, "task", "Build", issueTitle));

        return (project, issue, sessionName, workflowRunId);
    }

    private async Task<T> PostRawAsync<T>(string path, object body)
    {
        using var response = await _client.PostAsJsonAsync(path, body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
    }

    private static string RunnerAgentSessionAttachPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"{RunnerSessionPath(runnerId, projectId, workflowRunId, sessionName)}/attach";

    private static string RunnerAgentSessionRuntimeEventsPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"{RunnerSessionPath(runnerId, projectId, workflowRunId, sessionName)}/runtime-events";

    private static string RunnerSessionPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}";

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private sealed record RunnerAgentSessionDto(RunnerAgentSessionKeyDto Key, string? AcpSessionId, string Status, string? WorkDir, string? Model);
    private sealed record RunnerAgentSessionKeyDto(string ProjectId, string WorkflowRunId, string SessionName);
    private sealed record WorkflowSessionDto(string Id, string WorkflowRunId, string SessionName, string? AgentSessionId, string Status, string? Model);
    private sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, IssueSessionTranscriptTestResponse Transcript);
    private sealed record SessionEventDto(long Sequence, string Type, string? WorkId);
    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record IssueSessionMetadataTestDto(string Id, string SessionName, IssueSessionMetadataCountsTestDto Metadata);
    private sealed record IssueSessionMetadataCountsTestDto(int SegmentCount, int ToolCount);
    private sealed record IssueSessionTranscriptTestResponse(IssueSessionTranscriptTurnTestDto[] Turns, int SegmentCount, string? LastActivityAt);
    private sealed record IssueSessionTranscriptTurnTestDto(string Id, string StartedAt, string? CompletedAt, bool Incomplete, IssueSessionTranscriptUserTestDto User, IssueSessionTranscriptPartTestDto[] Assistant);
    private sealed record IssueSessionTranscriptUserTestDto(string Text, string Kind, string SentAt);
    private sealed record IssueSessionTranscriptPartTestDto(string Id, string Type, string? Text, string? ToolCallId, string? Status, string? StartedAt, string? CompletedAt, string? Message, string? Kind, string? At);
}
