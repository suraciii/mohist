using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Issue.Api;

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
            Uses: "mohist/opencode",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        var info = await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, issue.Number, work.WorkflowRunId, sessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));
        var session = new CreatedSession(project.Id, issue.Number, work.WorkflowRunId, sessionName, info);
        if (start)
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = session.Id, runtime = "opencode", expectedRuntime = "opencode", expectedRuntimeSessionId = (string?)null, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        return (project, issue, work, session);
    }

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    private string WorkflowRuntimeEventsPath(CreatedSession session, string workflowRunId) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    private async Task<string> AcceptWorkflowInputAsync(CreatedSession session, string workflowRunId, string inputDeliveryId, string prompt)
    {
        using var response = await _client.PostAsJsonAsync(
            WorkflowRuntimeEventsPath(session, workflowRunId),
            new
            {
                runtimeSessionId = session.Id,
                runtime = "opencode",
                agentSessionId = session.Id,
                inputDeliveryId,
                actionAttemptId = session.SessionName,
                workId = session.SessionName,
                runtimeEvents = new object[]
                {
                    new { type = "session.input", payload = new { text = prompt } }
                }
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"workflow input failed: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        var receipts = document.RootElement.EnumerateArray().ToArray();
        if (receipts.Length == 1)
            return receipts[0].GetProperty("agentTurnId").GetString()!;
        var turns = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).ListTurnsAsync();
        return turns.Single(turn => turn.WorkflowExecution?.InputDeliveryId == inputDeliveryId).WorkflowExecution!.AgentTurnId;
    }

    private string RunnerSessionRuntimeEventsPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/agent-sessions/{session.Id}/runtime-events";

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
            issueNumber,
            runtime = "opencode"
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
}
