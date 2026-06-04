using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

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

    [Fact]
    public async Task GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var sessionName = "builder";

        var ensured = await PostRawAsync<WorkflowSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/ensure", new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            title = "Generate proposal",
            issueNumber = 7,
        });
        await PostRawAsync<WorkflowSessionDto>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/attach", new
        {
            agentSessionId = "acp-1",
            workDir = "/workspace",
            model = "openai/gpt-4o",
            processPid = 123,
        });

        await PostRawAsync<SessionEventDto[]>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/events", new
        {
            workId = "proposal",
            workType = "task",
            stage = "plan",
            events = new object[]
            {
                new { type = "mohist_prompt", payload = new { text = "write proposal" } },
                new { type = "agent_message_chunk", payload = new { content = new { text = "done" } } },
            },
        });
        await PostRawAsync<SessionEventDto[]>($"/api/runner/runner-1/sessions/{projectId}/{workflowRunId}/{sessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            },
        });

        var detail = await _client.GetDataAsync<WorkflowSessionDetailDto>($"/api/workflow-runs/{workflowRunId}/sessions/{sessionName}");

        Assert.Equal(workflowRunId, ensured.WorkflowRunId);
        Assert.Equal(sessionName, detail.Session.SessionName);
        Assert.Equal("acp-1", detail.Session.AgentSessionId);
        Assert.Equal("completed", detail.Session.Status);
        Assert.Equal("openai/gpt-4o", detail.Session.Model);
        Assert.Equal([1, 2, 3], detail.Events.Select(e => e.Sequence).ToArray());
        Assert.Equal(["mohist_prompt", "agent_message_chunk", "agent_session_terminal"], detail.Events.Select(e => e.Type).ToArray());
        Assert.Equal("proposal", detail.Events[0].WorkId);
        Assert.Equal("proposal", detail.Events[1].WorkId);
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
        var sessionId = GrainKey.WorkflowAgentSession(project.Id, workflowRunId, sessionName);

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{workflowRunId}/{sessionName}/attach", new
        {
            agentSessionId = sessionId,
            workDir = project.Path,
            processPid = 1234
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{workflowRunId}/{sessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new { text = promptBody, kind = "task" }
                },
                new { type = "agent_message_chunk", payload = new { text = "starting work" } },
                new
                {
                    type = "agent_liveness_status",
                    payload = new { status = "probing", probeDeadlineAt = "2026-06-03T12:00:00Z", lastActivityType = "session" }
                },
                new
                {
                    type = "agent_liveness_status",
                    payload = new { status = "failed", failureReason = "no progress", lastActivityType = "message" }
                },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "failed", failureReason, exitCode = 1 }
                }
            }
        });

        var metadata = await _client.GetDataAsync<IssueSessionMetadataTestDto>($"/api/issues/{issue.Number}/sessions/{sessionName}?projectId={project.Id}");
        Assert.Equal(sessionId, metadata.Id);
        Assert.Equal(sessionName, metadata.SessionName);
        Assert.Equal(5, metadata.Metadata.EventCount);
        Assert.Equal(0, metadata.Metadata.ToolCount);

        var events = await _client.GetDataAsync<IssueSessionEventsTestResponse>($"/api/issues/{issue.Number}/sessions/{sessionName}/events?projectId={project.Id}");
        Assert.Equal(5, events.Events.Length);
        Assert.Equal("mohist_prompt", events.Events[0].Type);
        Assert.Equal(promptBody, events.Events[0].Payload?.GetProperty("text").GetString());
        Assert.Equal("task", events.Events[0].Payload?.GetProperty("kind").GetString());
        Assert.Equal("agent_session_terminal", events.Events[^1].Type);
        Assert.Equal(failureReason, events.Events[^1].Payload?.GetProperty("failureReason").GetString());
    }

    private async Task<(ProjectDto Project, IssueDto Issue, string SessionName, string WorkflowRunId)> CreateIssueWorkflowSessionAsync(string name, string? title = null)
    {
        var projectName = $"workflow-session-{name}-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new
        {
            name = projectName,
            path = Directory.GetCurrentDirectory(),
            baseBranch = "main"
        });
        var issueTitle = title ?? $"Workflow session {name}";
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new
        {
            title = issueTitle,
            body = "track workflow session",
            labels = Array.Empty<string>(),
            priority = "p1",
            projectId = project.Id
        });

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>($"{project.Id}:{issue.Number}");
        await issueGrain.StartWorkAsync();
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var sessionName = $"task-{Guid.NewGuid():N}";
        var sessionId = GrainKey.WorkflowAgentSession(project.Id, workflowRunId, sessionName);
        await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(sessionId)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(
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

    private sealed record WorkflowSessionDto(string Id, string WorkflowRunId, string SessionName, string? AgentSessionId, string Status, string? Model);
    private sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, SessionEventDto[] Events);
    private sealed record SessionEventDto(long Sequence, string Type, string? WorkId);
    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record IssueSessionMetadataTestDto(string Id, string SessionName, IssueSessionMetadataCountsTestDto Metadata);
    private sealed record IssueSessionMetadataCountsTestDto(int EventCount, int ToolCount);
    private sealed record IssueSessionEventsTestResponse(IssueSessionEventTestDto[] Events);
    private sealed record IssueSessionEventTestDto(long Id, long Sequence, string Type, JsonElement? Payload, string CreatedAt);
}
