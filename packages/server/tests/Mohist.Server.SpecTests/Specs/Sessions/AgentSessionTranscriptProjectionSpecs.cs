using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("PlatformIntegration")]
public class AgentSessionTranscriptProjectionSpecs : AgentSessionTestSupport
{
    public AgentSessionTranscriptProjectionSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task LoadLatestEventsActivity_DoesNotSuppressTerminalOrLivenessEventTypes()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("activity-no-filter", title: "Activity no filter");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        // Under the activity model `session.closed` is a no-op (the grain no
        // longer persists a transcript part for it); a liveness-bearing event
        // such as `session.liveness` still produces a `status` transcript
        // part, so verify that part type is forwarded to the activity feed
        // rather than suppressed.
        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "session.liveness",
                    payload = new { }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, persistence);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.NotNull(card.LastActivity);
        Assert.Equal("status", card.LastActivity!.Text);
    }

    [Fact]
    public async Task IssueSessionMetadataEndpoint_ProjectsTranscriptEventsInSequenceOrder_WhenRowsWereInsertedOutOfOrder()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("metadata-order", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await SeedOutOfOrderTranscriptPartsAsync(dbFactory, currentSession.Id);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var eventSummary = doc.RootElement.GetProperty("data").GetProperty("eventSummary");

        Assert.Equal("sequence-last-model", eventSummary.GetProperty("resolvedModel").GetString());
        Assert.Equal("sequence-last-failure", eventSummary.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task RuntimeEvents_RefreshSessionSummaryActivityWithoutDomainEvents()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("summary-activity", sessionName: "check");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);
        var beforeSummaries = await _client.GetDataAsync<AgentSessionSummaryDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        var beforeSummary = Assert.Single(beforeSummaries);
        Assert.NotNull(beforeSummary.LastDataAt);
        var beforeLastDataAt = DateTime.Parse(beforeSummary.LastDataAt!);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "still working" } }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, persistence);

        var summaries = await _client.GetDataAsync<AgentSessionSummaryDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        var summary = Assert.Single(summaries);

        // Under the activity model a `message.delta` only refreshes
        // LastDataAt (RecordActivity) without mutating the activity value;
        // a freshly attached session that never received a session.input /
        // session.activity event stays idle.
        Assert.Equal("idle", summary.Status);
        Assert.NotNull(summary.LastDataAt);
        Assert.True(DateTime.Parse(summary.LastDataAt!) > beforeLastDataAt);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");
        using var document = JsonDocument.Parse(raw);
        var wireSummary = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(session.Id, wireSummary.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", wireSummary.GetProperty("runtime").GetString());
        Assert.False(wireSummary.TryGetProperty("coderSessionId", out _));
    }

    [Fact]
    public async Task CoderSessionSummary_UnboundSessionLeavesRuntimeSessionIdNull()
    {
        var (project, issue, _, _) = await CreateStartedAgentSessionAsync("unbound-summary", start: false, sessionName: "plan");

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/coder-sessions");

        using var document = JsonDocument.Parse(raw);
        var summary = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.False(summary.TryGetProperty("runtimeSessionId", out _));
    }

    [Fact]
    public async Task UnboundQueuedSessionTranscript_ProjectsCanonicalTurnWithoutRuntimeBinding()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("unbound-transcript", start: false, sessionName: "plan");
        var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await sessionGrain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: $"input-{session.Id}",
            TurnId: $"turn-{session.Id}",
            Prompt: "queued task",
            Source: "agent-launch",
            JobId: work.WorkId,
            Runtime: "opencode",
            WorkDir: $"/workspaces/{project.Id}"));

        var raw = await _client.GetRawAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");
        using var document = JsonDocument.Parse(raw);
        var data = document.RootElement.GetProperty("data");
        var turn = Assert.Single(data.GetProperty("turns").EnumerateArray());
        Assert.Equal("queued", data.GetProperty("status").GetString());
        Assert.Equal("queued", turn.GetProperty("status").GetString());
        Assert.True(turn.GetProperty("incomplete").GetBoolean());
        Assert.Equal("queued task", turn.GetProperty("user").GetProperty("text").GetString());
    }

    [Fact]
    public async Task IssueSessionMetadataEndpoint_MissingSession_ReturnsNotFound()
    {
        var projectName = $"metadata-not-found-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Metadata not found", projectId = project.Id });

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RunnerAppendsManyChunks_PersistsAggregatedTranscriptSegmentsOnly()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("chunk-aggregation", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var session = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await sessionGrain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "input-canonical",
            TurnId: "turn-canonical",
            Prompt: "plan the refactor",
            Source: "workflow",
            JobId: work.WorkId,
            Runtime: "opencode",
            WorkDir: $"/workspaces/{project.Id}"));
        var persistence = _fixture.Persistence.Checkpoint(session.Id);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        var runtimeEvents = Enumerable.Range(0, 96)
            .Select(i => new { type = "reasoning.delta", payload = new { text = i.ToString("D2"), messageId = "reasoning-1" } })
            .Cast<object>()
            .ToArray();

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new { runtimeSessionId = session.Id, runtimeEvents });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 1, persistence);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await LoadTranscriptPartsAsync(db, session.Id);

        var part = Assert.Single(parts);
        Assert.Equal("reasoning", part.Type);
        Assert.Equal(96, part.RawEventCount);
        Assert.Equal(string.Concat(Enumerable.Range(0, 96).Select(i => i.ToString("D2"))), part.Text);

        var response = await _client.GetDataAsync<AgentSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{session.IssueNumber}/sessions/{session.SessionName}/transcript");
        var turn = Assert.Single(response.Turns);
        var transcriptPart = Assert.Single(turn.Assistant);
        Assert.Equal("reasoning", transcriptPart.Type);
        Assert.Equal(string.Concat(Enumerable.Range(0, 96).Select(i => i.ToString("D2"))), transcriptPart.Text);
    }

    [Fact]
    public async Task DeferredPersistence_SessionDetailTranscriptContainsAllTextAndToolParts()
    {
        var (project, issue, work, _) = await CreateStartedAgentSessionAsync("deferred-transcript", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var session = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, "plan", work, "Plan session");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "[mohist-workspace-anchor]\n/workspaces/internal\n[/mohist-workspace-anchor]\n\ninternal system prompt\n\nplan the refactor", kind = "task" } },
                new { type = "message.delta", payload = new { text = "first", messageId = "msg-1" } },
                new { type = "message.delta", payload = new { text = " second", messageId = "msg-1" } },
                new { type = "reasoning.delta", payload = new { text = "thinking", messageId = "reason-1" } },
                new { type = "reasoning.delta", payload = new { text = "deeper", messageId = "reason-2" } }
            }
        });

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
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
                    type = "tool_call.completed",
                    payload = new
                    {
                        toolCallId = "tool-1",
                        kind = "read",
                        status = "completed",
                        title = "Read README",
                        rawOutput = new { content = "# Project" }
                    }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 4, persistence);

        await using var db = await dbFactory.CreateDbContextAsync();
        var dbParts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal(4, dbParts.Length);
        Assert.Equal(["text", "reasoning", "reasoning", "tool"], dbParts.Select(p => p.Type).ToArray());

        var response = await _client.GetDataAsync<AgentSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");

        Assert.Equal(4, response.PartCount);
        var turn = Assert.Single(response.Turns);
        Assert.Equal("mohist", turn.User.Role);
        Assert.Equal("task", turn.User.Kind);
        Assert.Equal("plan the refactor", turn.User.Text);

        Assert.Equal(4, turn.Assistant.Length);
        Assert.Equal("text", turn.Assistant[0].Type);
        Assert.Equal("first second", turn.Assistant[0].Text);
        Assert.Equal("reasoning", turn.Assistant[1].Type);
        Assert.Equal("thinking", turn.Assistant[1].Text);
        Assert.Equal("reasoning", turn.Assistant[2].Type);
        Assert.Equal("deeper", turn.Assistant[2].Text);
        Assert.Equal("tool", turn.Assistant[3].Type);
        var toolPart = turn.Assistant[3].Tool;
        Assert.NotNull(toolPart);
        Assert.Equal("tool-1", toolPart.ToolCallId);
        Assert.Equal("read", toolPart.ToolName);
        Assert.Equal("completed", toolPart.Status);
        Assert.Equal("Read README", toolPart.Title);

        var publicJson = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript");
        using (var publicDocument = JsonDocument.Parse(publicJson))
        {
            var publicTurn = publicDocument.RootElement.GetProperty("data").GetProperty("turns")[0];
            Assert.Equal("plan the refactor", publicTurn.GetProperty("user").GetProperty("text").GetString());
            Assert.False(publicTurn.GetProperty("assistant")[3].GetProperty("tool").TryGetProperty("rawInput", out _));
        }

        var rawJson = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript?view=raw");
        using var rawDocument = JsonDocument.Parse(rawJson);
        var rawTurn = rawDocument.RootElement.GetProperty("data").GetProperty("turns")[0];
        Assert.Contains("mohist-workspace-anchor", rawTurn.GetProperty("user").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("README.md", rawTurn.GetProperty("assistant")[3].GetProperty("tool").GetProperty("rawInput").GetString(), StringComparison.Ordinal);
    }

}
