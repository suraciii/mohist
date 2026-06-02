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

        var detail = await _client.GetDataAsync<WorkflowSessionDetailDto>($"/api/workflows/{workflowRunId}/sessions/{sessionName}");

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
    public async Task GivenMohistPromptAndTerminalFailure_WhenIssueWorkflowSessionIsQueried_ThenTurnsReflectsEventStreamAndTurnCount()
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

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/workflow/sessions/{sessionName}?projectId={project.Id}");
        Assert.Equal(sessionId, detail.Id);
        Assert.Equal(sessionName, detail.SessionName);

        var turns = detail.Turns.EnumerateArray().ToArray();
        var turn = Assert.Single(turns);
        var user = turn.GetProperty("user");
        Assert.Equal(promptBody, user.GetProperty("text").GetString());
        Assert.Equal("task", user.GetProperty("kind").GetString());
        Assert.Equal("mohist", user.GetProperty("role").GetString());

        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("starting work"));
        var probeIndex = Array.FindIndex(assistant, p =>
            p.GetProperty("type").GetString() == "error"
            && p.GetProperty("kind").GetString() == "recovery"
            && p.GetProperty("message").GetString()!.Contains("Liveness probe sent"));
        var livenessFailedIndex = Array.FindIndex(assistant, p =>
            p.GetProperty("type").GetString() == "error"
            && p.GetProperty("kind").GetString() == "recovery"
            && p.GetProperty("message").GetString()!.Contains("Liveness failed"));
        var terminalIndex = Array.FindIndex(assistant, p =>
            p.GetProperty("type").GetString() == "error"
            && p.GetProperty("kind").GetString() == "failed");
        Assert.True(probeIndex >= 0);
        Assert.True(livenessFailedIndex >= 0);
        Assert.True(terminalIndex >= 0);
        Assert.Equal(failureReason, assistant[terminalIndex].GetProperty("message").GetString());
        Assert.False(string.IsNullOrEmpty(turn.GetProperty("completedAt").GetString()));
        var textIndex = Array.FindIndex(assistant, p => p.GetProperty("type").GetString() == "text");
        Assert.True(textIndex < probeIndex);
        Assert.True(probeIndex < livenessFailedIndex);
        Assert.True(livenessFailedIndex < terminalIndex);

        Assert.Equal(1, detail.Metadata.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public async Task GivenLegacySessionWithoutMohistPrompt_WhenIssueWorkflowSessionIsQueried_ThenTranscriptReturnsLegacyMissingTurn()
    {
        const string shortSessionTitle = "Cover backend projection and progress behavior";
        var (project, issue, sessionName, workflowRunId) = await CreateIssueWorkflowSessionAsync("workflow-legacy-missing", title: shortSessionTitle);
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
                new { type = "agent_message_chunk", payload = new { text = "legacy hello" } },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/workflow/sessions/{sessionName}?projectId={project.Id}");
        var turns = detail.Turns.EnumerateArray().ToArray();
        var turn = Assert.Single(turns);

        var user = turn.GetProperty("user");
        Assert.Equal("legacy-missing", user.GetProperty("kind").GetString());
        Assert.Equal("mohist", user.GetProperty("role").GetString());
        Assert.Equal("Prompt was not recorded for this historical session", user.GetProperty("text").GetString());
        Assert.NotEqual(shortSessionTitle, user.GetProperty("text").GetString());
        Assert.NotEqual(sessionName, user.GetProperty("text").GetString());
        Assert.NotEqual(sessionId, user.GetProperty("text").GetString());
        Assert.Equal("legacy-missing", user.GetProperty("summary").GetProperty("kind").GetString());

        var assistant = turn.GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("legacy hello"));
        Assert.Contains(assistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "completed");

        Assert.Equal(1, detail.Metadata.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public async Task GivenTwoMohistPromptEvents_WhenIssueWorkflowSessionIsQueried_ThenTranscriptProducesTwoTurnsInEventOrder()
    {
        var (project, issue, sessionName, workflowRunId) = await CreateIssueWorkflowSessionAsync("workflow-two-prompts");
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
                    payload = new { text = "first mohist prompt body", kind = "task" }
                },
                new { type = "agent_message_chunk", payload = new { text = "first response" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{workflowRunId}/{sessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "mohist_prompt",
                    payload = new { text = "second mohist prompt body", kind = "followup" }
                },
                new { type = "agent_message_chunk", payload = new { text = "second response" } },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "completed", exitCode = 0 }
                }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/workflow/sessions/{sessionName}?projectId={project.Id}");
        var turns = detail.Turns.EnumerateArray().ToArray();
        Assert.Equal(2, turns.Length);

        Assert.Equal("first mohist prompt body", turns[0].GetProperty("user").GetProperty("text").GetString());
        Assert.Equal("task", turns[0].GetProperty("user").GetProperty("kind").GetString());
        var firstAssistant = turns[0].GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(firstAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("first response"));
        Assert.DoesNotContain(firstAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("second response"));

        Assert.Equal("second mohist prompt body", turns[1].GetProperty("user").GetProperty("text").GetString());
        Assert.Equal("followup", turns[1].GetProperty("user").GetProperty("kind").GetString());
        var secondAssistant = turns[1].GetProperty("assistant").EnumerateArray().ToArray();
        Assert.Contains(secondAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("second response"));
        Assert.DoesNotContain(secondAssistant, p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains("first response"));
        Assert.Contains(secondAssistant, p => p.GetProperty("type").GetString() == "error" && p.GetProperty("kind").GetString() == "completed");

        Assert.Equal(2, detail.Metadata.GetProperty("turnCount").GetInt32());
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
    private sealed record WorkflowAgentSessionTranscript(string Id, string SessionName, JsonElement Turns, JsonElement Metadata);
}