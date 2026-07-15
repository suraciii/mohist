using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue2")]
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
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "Plan session", kind = "task" } },
                new { type = "message.delta", payload = new { text = "hello" } },
                new
                {
                    type = "usage.updated",
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
                    type = "model.resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call.started",
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
                    type = "tool_call.updated",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "failed", title = "Read README" }
                },
                new
                {
                    type = "session.closed",
                    payload = new { status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1 }
                },
                new { type = "message.delta", payload = new { text = "world" } }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(currentSession.Id, 5, _fixture.Grains);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");

        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        Assert.Equal("plan", root.GetProperty("sessionName").GetString());
        Assert.Equal(currentSession.Id, root.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", root.GetProperty("runtime").GetString());
        Assert.False(root.TryGetProperty("acpSessionId", out _));
        Assert.False(root.TryGetProperty("coderSessionId", out _));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("status").GetString()));
        Assert.Equal(work.Stage, root.GetProperty("stage").GetString());
        Assert.Equal("Plan session", root.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("createdAt").GetString()));
        Assert.False(root.TryGetProperty("completedAt", out _));
        Assert.True(root.TryGetProperty("model", out _));
        var eventSummary = root.GetProperty("eventSummary");
        var usage = root.GetProperty("usage");
        Assert.Equal("anthropic/claude-sonnet-4", eventSummary.GetProperty("resolvedModel").GetString());
        Assert.Equal(100, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(50, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(150, usage.GetProperty("totalTokens").GetInt64());
        Assert.Equal(10, usage.GetProperty("cachedReadTokens").GetInt64());
        Assert.Equal(5, usage.GetProperty("thoughtTokens").GetInt64());
        Assert.Equal(0.01, usage.GetProperty("costAmount").GetDouble());
        Assert.Equal("USD", usage.GetProperty("costCurrency").GetString());
        Assert.Equal(150, usage.GetProperty("contextWindowUsed").GetInt64());
        Assert.Equal(200000, usage.GetProperty("contextWindowSize").GetInt64());
        Assert.Equal("probe_timeout", eventSummary.GetProperty("failureCategory").GetString());
        Assert.Equal(1, eventSummary.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, eventSummary.GetProperty("toolErrorCount").GetInt32());

        var metadata = root.GetProperty("metadata");
        // 6 parts: session.input, message.delta (hello+world merged),
        // usage.updated, model.resolved, tool_call (started+updated),
        // session.closed, plus the context_health_update snapshot
        // emitted by the grain on the first usage event.
        Assert.Equal(6, metadata.GetProperty("partCount").GetInt32());
        Assert.Equal(1, metadata.GetProperty("toolCount").GetInt32());

        Assert.False(root.TryGetProperty("events", out _));
        Assert.False(root.TryGetProperty("turns", out _));
        Assert.False(root.TryGetProperty("assistant", out _));
        Assert.False(root.TryGetProperty("workflowLogs", out _));
        Assert.False(root.TryGetProperty("transcript", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueSessionMetadataEndpoint_ExposesContextExhaustionFailureCategory()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("exhaustion-shape", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        // Drive usage to 96% (>= 90% threshold) then close the
        // session as failed. The grain's classifier must rewrite
        // the failureCategory to "context_exhaustion" and the API
        // response must surface it.
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        contextWindowSize = 1000L,
                        contextWindowUsed = 960L
                    }
                },
                new
                {
                    type = "session.closed",
                    payload = new { status = "failed", exitCode = 1 }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(currentSession.Id, 2, _fixture.Grains);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");
        var eventSummary = root.GetProperty("eventSummary");
        var usage = root.GetProperty("usage");

        Assert.Equal("context_exhaustion", eventSummary.GetProperty("failureCategory").GetString());
        Assert.Equal(960, usage.GetProperty("contextWindowUsed").GetInt64());
        Assert.Equal(1000, usage.GetProperty("contextWindowSize").GetInt64());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueSessionEventsEndpoint_ReturnsTranscriptSegmentsInAscendingSequenceAcrossBatches()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("raw-events-ordering", sessionName: "build");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(issue.Id);
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "build", work, "Build session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "do the thing", kind = "task" } },
                new
                {
                    type = "tool_call.started",
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
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "first" } },
                new { type = "reasoning.delta", payload = new { content = new { text = "thinking" } } },
                new
                {
                    type = "tool_call.updated",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        rawOutput = new { text = "result" }
                    }
                },
                new { type = "message.delta", payload = new { text = "second" } }
            }
        });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(currentSession.Id, 4, _fixture.Grains);

        var response = await _client.GetDataAsync<IssueSessionTranscriptResponseDto>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/transcript");

        Assert.Equal(4, response.PartCount);
        var turn = Assert.Single(response.Turns);
        Assert.Equal("do the thing", turn.User.Text);
        Assert.Equal("task", turn.User.Kind);
        Assert.Equal(
            new[] { "tool", "text", "reasoning" },
            turn.Assistant.Select(p => p.Type).ToArray());
        Assert.Contains(turn.Assistant, p => p.Type == "tool" && p.Tool?.ToolCallId == "tool-1" && p.Tool.Status == "completed");
        Assert.Null(turn.CompletedAt);
        Assert.NotNull(response.LastActivityAt);
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
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "do the thing", kind = "task" } },
                new
                {
                    type = "tool_call.started",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "in_progress",
                        title = "Read README",
                        rawInput = new { filePath = "README.md" }
                    }
                },
                new { type = "message.delta", payload = new { text = "first" } },
                new { type = "reasoning.delta", payload = new { content = new { text = "thinking" } } }
            }
        });

        var metadataRaw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        var transcriptRaw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");

        using (var metadataDoc = JsonDocument.Parse(metadataRaw))
        using (var transcriptDoc = JsonDocument.Parse(transcriptRaw))
        {
            var metadataRoot = metadataDoc.RootElement.GetProperty("data");
            AssertNoProjectionFields(metadataRoot, "metadata");
            Assert.False(metadataRoot.TryGetProperty("turns", out _));
            Assert.False(metadataRoot.TryGetProperty("assistant", out _));
            Assert.False(metadataRoot.TryGetProperty("workflowLogs", out _));
            Assert.False(metadataRoot.TryGetProperty("events", out _));

            var transcriptRoot = transcriptDoc.RootElement.GetProperty("data");
            Assert.True(transcriptRoot.TryGetProperty("turns", out _));
            Assert.True(transcriptRoot.TryGetProperty("partCount", out _));
            Assert.False(transcriptRoot.TryGetProperty("lastActivityAt", out _));
            Assert.False(transcriptRoot.TryGetProperty("events", out _));
            Assert.False(transcriptRoot.TryGetProperty("assistant", out _));
            Assert.False(transcriptRoot.TryGetProperty("workflowLogs", out _));
            Assert.False(transcriptRoot.TryGetProperty("metadata", out _));
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

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"isa-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issueTitle = title ?? $"Session api {name}";
var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = issueTitle, body = "track session", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });

        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        var info = await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, issue.Number, work.WorkflowRunId, sessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));
        var session = new CreatedSession(project.Id, issue.Number, work.WorkflowRunId, sessionName, info);
        if (start)
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        return (project, issue, work, session);
    }

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    private string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    private string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

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
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

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

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }

    private sealed record IssueSessionTranscriptResponseDto(IssueSessionTranscriptTurnDto[] Turns, int PartCount, string? LastActivityAt);
    private sealed record IssueSessionTranscriptTurnDto(string Id, string StartedAt, string? CompletedAt, bool Incomplete, IssueSessionTranscriptUserDto User, IssueSessionTranscriptPartDto[] Assistant);
    private sealed record IssueSessionTranscriptUserDto(string Text, string Kind, string SentAt);
    private sealed record IssueSessionTranscriptPartDto(string Id, string Type, string? Text, IssueSessionTranscriptToolDto? Tool, string? Message, string? Kind, string? StartedAt, string? CompletedAt, string? At);
    private sealed record IssueSessionTranscriptToolDto(string ToolCallId, string ToolName, string Status, string? Title, string? Input, string? Output, string? Error, string StartedAt, string? CompletedAt);
}
