using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("IntegrationWorkflow")]
public sealed class WorkflowSessionLifecycleSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"workflow-session-lifecycle-{Guid.NewGuid():N}";

    public WorkflowSessionLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GivenPausedWorkflow_WhenRunnerConfirmsSessionStop_ThenWorkflowTaskIsIdleAndRerunIsUnlocked()
    {
        var active = await CreateActiveWorkflowSessionAsync("paused-session-stop");
        await active.Workflow.PauseAsync("user-pause");

        await _client.PostOkAsync(
            RuntimeEventsPath(active),
            new
            {
                runtimeSessionId = active.RuntimeSessionId,
                workId = active.Work.Id,
                workType = active.Work.WorkType,
                stage = active.Work.Stage,
                runtimeEvents = new object[]
                {
                    new
                    {
                        type = "session.activity",
                        payload = new
                        {
                            activity = "idle",
                            status = "completed",
                            source = "cancel",
                            stopConfirmed = true,
                            work = (object?)null,
                        }
                    }
                },
            });

        Assert.Equal("Paused", await active.Workflow.GetRunStatusAsync());
        Assert.Null(await active.Workflow.GetCurrentWorkIdAsync());
        Assert.Null(await active.Workflow.GetActiveWorkAsync(active.Work.Id!));
        Assert.Equal("idle", Assert.Single(await ListWorkflowSessionsAsync(active.WorkflowRunId)).Status);

        var rerun = await active.Workflow.RerunFromStageAsync(active.Work.Stage);
        Assert.True(rerun.Success, rerun.Error);
        Assert.Null(await active.Workflow.GetCurrentWorkIdAsync());
    }

    [Fact]
    public async Task GivenRunningWorkflow_WhenSessionReportsFailedIdle_ThenWorkflowTaskFailsAndLateReportIsStale()
    {
        var active = await CreateActiveWorkflowSessionAsync("runtime-failure-session");

        await _client.PostOkAsync(
            RuntimeEventsPath(active),
            new
            {
                runtimeSessionId = "stale-runtime-session",
                runtimeEvents = new object[]
                {
                    new { type = "turn.failed", payload = new { status = "failed", failureReason = "pending apply_patch failed" } },
                    new { type = "session.activity", payload = new { activity = "idle", status = "failed", exitCode = 1, failureReason = "pending apply_patch failed" } },
                },
            });

        Assert.Equal(active.Work.Id, await active.Workflow.GetCurrentWorkIdAsync());
        Assert.NotNull(await active.Workflow.GetActiveWorkAsync(active.Work.Id!));

        await _client.PostOkAsync(
            RuntimeEventsPath(active),
            new
            {
                runtimeSessionId = active.RuntimeSessionId,
                runtimeEvents = new object[]
                {
                    new { type = "turn.failed", payload = new { status = "failed", failureReason = "pending apply_patch failed" } },
                    new { type = "session.activity", payload = new { activity = "idle", status = "failed", exitCode = 1, failureReason = "pending apply_patch failed" } },
                },
            });

        Assert.Null(await active.Workflow.GetCurrentWorkIdAsync());
        Assert.Null(await active.Workflow.GetActiveWorkAsync(active.Work.Id!));
        Assert.Equal(
            ReportAck.Stale,
            await active.Workflow.AbandonActiveWorkAsync(active.RunnerId, active.Work.Id!, "duplicate-session-failure"));
        Assert.Equal(
            ReportAck.Stale,
            await active.Workflow.ReceiveTaskReportAsync(active.RunnerId, active.Work.Id!, new TaskReport(
                active.Work.Id!,
                TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: "late runner failure")));
    }

    [Fact]
    public async Task GivenRunningWorkflow_WhenSessionReportsCompletedIdle_ThenTaskReportRemainsAuthoritative()
    {
        var active = await CreateActiveWorkflowSessionAsync("runtime-success-session");

        await _client.PostOkAsync(
            RuntimeEventsPath(active),
            new
            {
                runtimeSessionId = active.RuntimeSessionId,
                runtimeEvents = new object[]
                {
                    new { type = "session.activity", payload = new { activity = "idle", status = "completed", exitCode = 0 } },
                },
            });

        Assert.Equal(active.Work.Id, await active.Workflow.GetCurrentWorkIdAsync());
        Assert.NotNull(await active.Workflow.GetActiveWorkAsync(active.Work.Id!));

        Assert.Equal(
            ReportAck.Accepted,
            await active.Workflow.ReceiveTaskReportAsync(active.RunnerId, active.Work.Id!, new TaskReport(
                active.Work.Id!,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null)));
        Assert.Null(await active.Workflow.GetCurrentWorkIdAsync());
    }

    [Fact]
    public async Task GivenPausedWorkflow_WhenQueuedSessionCancelCompletes_ThenWorkflowTaskIsReleased()
    {
        var active = await CreateActiveWorkflowSessionAsync("paused-session-cancel");
        await active.Workflow.PauseAsync("user-pause");

        const string turnId = "queued-cancel-turn";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(active.SessionId);
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "queued-cancel-input", turnId, "queued", "test"));

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{active.Project.Id}/agent-sessions/{active.SessionId}/cancel",
            new { turnId });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("cancelled", document.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("Paused", await active.Workflow.GetRunStatusAsync());
        Assert.Null(await active.Workflow.GetCurrentWorkIdAsync());
        Assert.Null(await active.Workflow.GetActiveWorkAsync(active.Work.Id!));

        var rerun = await active.Workflow.RerunFromStageAsync(active.Work.Stage);
        Assert.True(rerun.Success, rerun.Error);
    }

    [Fact]
    public async Task GivenPausedWorkflow_WhenTreeStopSeesNoTurn_ThenAlreadyIdleStillReleasesWorkflowWork()
    {
        var active = await CreateActiveWorkflowSessionAsync("paused-tree-stop");
        await active.Workflow.PauseAsync("user-pause");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{active.Project.Id}/agent-sessions/{active.SessionId}/stop");
        request.Headers.Add("Idempotency-Key", $"paused-tree-stop-{Guid.NewGuid():N}");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("completed", data.GetProperty("status").GetString());
        var target = Assert.Single(data.GetProperty("targets").EnumerateArray());
        Assert.Equal("alreadyidle", target.GetProperty("outcome").GetString());
        Assert.Equal("Paused", await active.Workflow.GetRunStatusAsync());
        Assert.Null(await active.Workflow.GetCurrentWorkIdAsync());
        Assert.Null(await active.Workflow.GetActiveWorkAsync(active.Work.Id!));
        Assert.Equal("idle", Assert.Single(await ListWorkflowSessionsAsync(active.WorkflowRunId)).Status);

        var rerun = await active.Workflow.RerunFromStageAsync(active.Work.Stage);
        Assert.True(rerun.Success, rerun.Error);
    }

    private async Task<ActiveWorkflowSession> CreateActiveWorkflowSessionAsync(string title)
    {
        var (project, issue, workflowRunId) = await CreateIssueWorkflowAsync(title);
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var assignment = await workflow.AssignWorkerAsync(_runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, assignment.Status);

        var work = await workflow.ClaimNextAsync(_runnerId)
            ?? throw new InvalidOperationException("The default workflow did not expose claimable work.");
        var workId = work.Id
            ?? throw new InvalidOperationException("Claimed workflow work did not have a work id.");
        var sessionName = $"task-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid().ToString("N");
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            "/workspace",
            Metadata: WorkflowSessionMetadata(
                project.Id, issue.Number, workflowRunId, sessionName, workId,
                work.WorkType, work.Stage, title)));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            ExpectedRunnerId: _runnerId,
            ExpectedRuntime: "opencode"));

        return new ActiveWorkflowSession(project, _runnerId, workflowRunId, sessionName, sessionId, runtimeSessionId, work, workflow);
    }

    private async Task<(ProjectDto Project, IssueDto Issue, string WorkflowRunId)> CreateIssueWorkflowAsync(string title)
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects", $"wfs-lifecycle-{Guid.NewGuid():N}");
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
            body = "track workflow session lifecycle",
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

    private Task<WorkflowSessionDto[]> ListWorkflowSessionsAsync(string workflowRunId) =>
        _client.GetDataAsync<WorkflowSessionDto[]>($"/api/workflow-runs/{workflowRunId}/sessions");

    private static string RuntimeEventsPath(ActiveWorkflowSession active) =>
        $"/api/runner/{active.RunnerId}/sessions/{Uri.EscapeDataString(active.Project.Id)}/{Uri.EscapeDataString(active.WorkflowRunId)}/{Uri.EscapeDataString(active.SessionName)}/runtime-events";

    private static AgentSessionMetadata WorkflowSessionMetadata(
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string workId,
        string workType,
        string stage,
        string title) =>
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

    private sealed record ActiveWorkflowSession(
        ProjectDto Project,
        string RunnerId,
        string WorkflowRunId,
        string SessionName,
        string SessionId,
        string RuntimeSessionId,
        WorkItem Work,
        IWorkflowGrain Workflow);

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title, string Status, string? WorkflowRunId);
    private sealed record WorkflowSessionDto(string Id, string WorkflowRunId, string SessionName, [property: System.Text.Json.Serialization.JsonPropertyName("activity")] string Status);
}
