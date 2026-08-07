using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

// Issue-458 T-002: two back-to-back Workflow OpenCode turns reuse
// the same logical AgentSession. Each turn records its own input,
// assistant/tool activity, and session.closed terminal event. The
// session.input persistence fence (T-001) splits the turns without
// depending on the 200 ms grain persist timer, and the latest accepted
// session.closed drives the Workflow session read model toward its
// terminal state. One final deterministic flush surfaces both turns
// through Workflow session reads.
[Collection("IntegrationWorkflow")]
public class WorkflowSessionTerminalConvergenceSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"workflow-session-terminal-convergence-{Guid.NewGuid():N}";

    public WorkflowSessionTerminalConvergenceSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GivenTwoBackToBackWorkflowTurns_WhenRuntimeEventsRouteIsPostedWithoutInterveningFlush_ThenBothTurnsPersistAndLatestCloseDrivesSessionStatus()
    {
        var (project, issue, sessionName, workflowRunId) = await CreateIssueWorkflowSessionAsync("workflow-two-turn-convergence");
        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);

        await _client.PostOkAsync(RunnerAgentSessionAttachPath(_runnerId, project.Id, workflowRunId, sessionName), new
        {
            runtimeSessionId = sessionId,
            workDir = $"/workspaces/{project.Id}",
            processPid = 1234
        });

        var persistence = _fixture.Persistence.Checkpoint(sessionId);
        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(_runnerId, project.Id, workflowRunId, sessionName), new
        {
            runtimeSessionId = sessionId,
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "first-prompt", kind = "task" } },
                new { type = "message.delta", payload = new { text = "first-answer" } },
                new
                {
                    type = "tool_call.started",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read README", rawInput = new { filePath = "README.md" } }
                },
                new
                {
                    type = "tool_call.updated",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "completed", rawOutput = new { text = "first-result" } }
                },
                new { type = "session.activity", payload = new { activity = "idle", status = "completed", exitCode = 0, operationId = "op-turn1" } }
            }
        });

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(_runnerId, project.Id, workflowRunId, sessionName), new
        {
            runtimeSessionId = sessionId,
            runtimeEvents = new object[]
            {
                new { type = "session.input", payload = new { text = "second-prompt", kind = "task" } },
                new { type = "message.delta", payload = new { text = "second-answer" } },
                new { type = "session.activity", payload = new { activity = "idle", status = "failed", failureReason = "second-turn-failure", exitCode = 1, operationId = "op-turn2" } }
            }
        });

        // No fake time-advance between posts; the input fence + a
        // single persistence observation inside WaitForTranscriptPartsAsync
        // surface both turns. Each turn contributes 3 (text+tool+close)
        // and 2 (text+close) parts — total 5.
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(sessionId, 5, persistence);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var turns = await db.AgentSessionTranscriptTurns
                .Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.Sequence)
                .ToListAsync();
            Assert.Equal(2, turns.Count);
            Assert.Equal("first-prompt", turns[0].PromptText);
            Assert.Equal("second-prompt", turns[1].PromptText);
            Assert.NotEqual(turns[0].Id, turns[1].Id);
        }

        var transcript = await _client.GetDataAsync<IssueSessionTranscriptTestResponse>($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}/transcript");
        Assert.Equal(2, transcript.Turns.Length);
        Assert.Equal("first-prompt", transcript.Turns[0].User.Text);
        Assert.Contains(transcript.Turns[0].Assistant, p => p.Type == "text" && p.Text == "first-answer");
        Assert.Contains(transcript.Turns[0].Assistant, p => p.Type == "tool");
        Assert.Equal("second-prompt", transcript.Turns[1].User.Text);
        Assert.Contains(transcript.Turns[1].Assistant, p => p.Type == "text" && p.Text == "second-answer");
        Assert.Contains(transcript.Turns[1].Assistant, p => p.Type == "error" && p.Kind == "failed" && p.Message == "second-turn-failure");

        // Under the activity model the Workflow session read no longer mirrors
        // a terminal session status: the session's activity settles on idle and
        // the workflow-session shape stopped projecting completedAt /
        // failureReason / exitCode (those belong to the AgentJob work result).
        // The second turn's failure fact is still observable in the transcript
        // error part asserted above.
        var workflowSessions = await _client.GetDataAsync<WorkflowSessionDto[]>($"/api/workflow-runs/{workflowRunId}/sessions");
        var listed = Assert.Single(workflowSessions);
        Assert.Equal("idle", listed.Status);
        Assert.Null(listed.ExitCode);
        Assert.Null(listed.FailureReason);
        Assert.Null(listed.CompletedAt);
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
        await _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        return (project, issue, workflowRunId);
    }

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private static string RunnerAgentSessionAttachPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"{RunnerSessionPath(runnerId, projectId, workflowRunId, sessionName)}/attach";

    private static string RunnerAgentSessionRuntimeEventsPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"{RunnerSessionPath(runnerId, projectId, workflowRunId, sessionName)}/runtime-events";

    private static string RunnerSessionPath(string runnerId, string projectId, string workflowRunId, string sessionName) =>
        $"/api/runner/{runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}";

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
    private sealed record IssueDto(string Id, int Number, string Title, string Status, string? WorkflowRunId);
    private sealed record WorkflowSessionDto(string Id, string WorkflowRunId, string SessionName, [property: System.Text.Json.Serialization.JsonPropertyName("activity")] string Status, string? CompletedAt, string? FailureReason, int? ExitCode);
    private sealed record IssueSessionTranscriptTestResponse(IssueSessionTranscriptTurnTestDto[] Turns);
    private sealed record IssueSessionTranscriptTurnTestDto(string Id, IssueSessionTranscriptUserTestDto User, IssueSessionTranscriptPartTestDto[] Assistant);
    private sealed record IssueSessionTranscriptUserTestDto(string Text, string Kind);
    private sealed record IssueSessionTranscriptPartTestDto(string Id, string Type, string? Text, string? Message, string? Kind);
}
