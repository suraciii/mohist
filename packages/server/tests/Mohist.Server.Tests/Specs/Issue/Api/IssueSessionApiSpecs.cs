using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueSessionApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"issue-session-api-{Guid.NewGuid():N}";

    public IssueSessionApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueSessionMetadataEndpoint_ExposesRequiredMetadataAndOmitsProjectedFields()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("metadata-shape", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(
                GrainKey.AgentSession(project.Id, currentWorkflowRunId, "plan"))
            .EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "plan", _runnerId, work.WorkId, work.WorkType, work.Stage, "Plan session"));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/attach", new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello" } },
                new
                {
                    type = "agent_usage_update",
                    payload = new
                    {
                        inputTokens = 100,
                        outputTokens = 50,
                        totalTokens = 150,
                        cachedReadTokens = 10,
                        thoughtTokens = 5,
                        costAmount = 0.01,
                        costCurrency = "USD",
                        contextWindowSize = 200000,
                        contextWindowUsed = 150
                    }
                },
                new
                {
                    type = "agent_session_model_resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new
                {
                    type = "tool_call_update",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "failed", title = "Read README" }
                },
                new
                {
                    type = "agent_session_terminal",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                },
                new { type = "agent_message_chunk", payload = new { text = "world" } }
            }
        });

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");

        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        Assert.Equal("plan", root.GetProperty("sessionName").GetString());
        Assert.Equal(currentSession.Id, root.GetProperty("acpSessionId").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("status").GetString()));
        Assert.Equal(work.Stage, root.GetProperty("stage").GetString());
        Assert.Equal("Plan session", root.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("createdAt").GetString()));
        Assert.True(root.TryGetProperty("completedAt", out _));
        Assert.True(root.TryGetProperty("model", out _));
        Assert.Equal("anthropic/claude-sonnet-4", root.GetProperty("resolvedModel").GetString());
        Assert.Equal(100, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(50, root.GetProperty("outputTokens").GetInt64());
        Assert.Equal(150, root.GetProperty("totalTokens").GetInt64());
        Assert.Equal(10, root.GetProperty("cachedReadTokens").GetInt64());
        Assert.Equal(5, root.GetProperty("thoughtTokens").GetInt64());
        Assert.Equal(0.01, root.GetProperty("costAmount").GetDouble());
        Assert.Equal("USD", root.GetProperty("costCurrency").GetString());
        Assert.Equal(150, root.GetProperty("contextWindowUsed").GetInt64());
        Assert.Equal(200000, root.GetProperty("contextWindowSize").GetInt64());
        Assert.Equal("probe_timeout", root.GetProperty("failureCategory").GetString());
        Assert.Equal(1, root.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, root.GetProperty("toolErrorCount").GetInt32());

        var metadata = root.GetProperty("metadata");
        Assert.Equal(7, metadata.GetProperty("eventCount").GetInt32());
        Assert.Equal(2, metadata.GetProperty("toolCount").GetInt32());

        Assert.False(root.TryGetProperty("events", out _));
        Assert.False(root.TryGetProperty("turns", out _));
        Assert.False(root.TryGetProperty("assistant", out _));
        Assert.False(root.TryGetProperty("workflowLogs", out _));
        Assert.False(root.TryGetProperty("transcript", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueSessionEventsEndpoint_ReturnsRawEventsInAscendingSequenceAcrossBatches()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("raw-events-ordering", sessionName: "build");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(
                GrainKey.AgentSession(project.Id, currentWorkflowRunId, "build"))
            .EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "build", _runnerId, work.WorkId, work.WorkType, work.Stage, "Build session"));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/attach", new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "mohist_prompt", payload = new { text = "do the thing", kind = "task" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README",
                        rawInput = new { filePath = "README.md" }
                    }
                }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "first" } },
                new { type = "agent_thought_chunk", payload = new { content = new { text = "thinking" } } },
                new
                {
                    type = "tool_call_update",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "result" }
                    }
                },
                new { type = "agent_message_chunk", payload = new { text = "second" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var response = await _client.GetDataAsync<IssueSessionEventsResponseDto>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/events");

        var types = response.Events.Select(e => e.Type).ToArray();
        Assert.Equal(
            new[] { "mohist_prompt", "tool_call", "agent_message_chunk", "agent_thought_chunk", "tool_call_update", "agent_message_chunk", "agent_session_terminal" },
            types);

        var sequences = response.Events.Select(e => e.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(s => s).ToArray(), sequences);
        Assert.Equal(Enumerable.Range(1, response.Events.Length).Select(i => (long)i).ToArray(), sequences);

        var prompt = response.Events.First(e => e.Type == "mohist_prompt");
        Assert.Equal("task", prompt.Payload?.GetProperty("kind").GetString());
        Assert.Equal("do the thing", prompt.Payload?.GetProperty("text").GetString());

        var toolUpdate = response.Events.First(e => e.Type == "tool_call_update");
        Assert.Equal("tool-1", toolUpdate.Payload?.GetProperty("toolCallId").GetString());
        Assert.Equal("completed", toolUpdate.Payload?.GetProperty("status").GetString());

        var terminal = response.Events.First(e => e.Type == "agent_session_terminal");
        Assert.Equal("completed", terminal.Payload?.GetProperty("status").GetString());
        Assert.Equal(0, terminal.Payload?.GetProperty("exitCode").GetInt32());

        foreach (var entry in response.Events)
        {
            Assert.True(entry.Id > 0);
            Assert.False(string.IsNullOrEmpty(entry.CreatedAt));
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueSessionApis_DoNotReturnServerProjectedTurns()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("removal-assertion", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(
                GrainKey.AgentSession(project.Id, currentWorkflowRunId, "plan"))
            .EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "plan", _runnerId, work.WorkId, work.WorkType, work.Stage, "Plan session"));
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/attach", new { agentSessionId = currentSession.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{currentSession.WorkflowRunId}/{currentSession.SessionName}/events", new
        {
            events = new object[]
            {
                new { type = "mohist_prompt", payload = new { text = "do the thing", kind = "task" } },
                new
                {
                    type = "tool_call",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new { type = "agent_message_chunk", payload = new { text = "first" } },
                new { type = "agent_thought_chunk", payload = new { content = new { text = "thinking" } } }
            }
        });

        var metadataRaw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        var eventsRaw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/events");

        using (var metadataDoc = JsonDocument.Parse(metadataRaw))
        using (var eventsDoc = JsonDocument.Parse(eventsRaw))
        {
            var metadataRoot = metadataDoc.RootElement.GetProperty("data");
            AssertNoProjectionFields(metadataRoot, "metadata");
            Assert.False(metadataRoot.TryGetProperty("turns", out _));
            Assert.False(metadataRoot.TryGetProperty("assistant", out _));
            Assert.False(metadataRoot.TryGetProperty("workflowLogs", out _));
            Assert.False(metadataRoot.TryGetProperty("events", out _));

            var eventsRoot = eventsDoc.RootElement.GetProperty("data");
            AssertNoProjectionFields(eventsRoot, "events");
            Assert.False(eventsRoot.TryGetProperty("turns", out _));
            Assert.False(eventsRoot.TryGetProperty("assistant", out _));
            Assert.False(eventsRoot.TryGetProperty("workflowLogs", out _));
            Assert.True(eventsRoot.TryGetProperty("events", out _));
        }
    }

    private static void AssertNoProjectionFields(JsonElement root, string label)
    {
        var serialized = root.GetRawText();
        Assert.DoesNotContain("\"turns\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"assistant\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"workflowLogs\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"transcript\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"BuildAssistantParts\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.True(root.ValueKind == JsonValueKind.Object, $"{label} response should be an object");
    }

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, AgentSessionInfo Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"isa-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issueTitle = title ?? $"Session api {name}";
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = issueTitle, body = "track session", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });

        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(project.Id, work.WorkflowRunId, sessionName));
        var session = await grain.EnsureAsync(new EnsureAgentSessionCommand(project.Id, issue.Number, work.WorkflowRunId, sessionName, _runnerId, work.WorkId, work.WorkType, work.Stage, work.Title));
        if (start)
            await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        return (project, issue, work, session);
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record IssueSessionEventsResponseDto(IssueSessionEventDto[] Events);
    private sealed record IssueSessionEventDto(long Id, long Sequence, string Type, JsonElement? Payload, string CreatedAt);
}
