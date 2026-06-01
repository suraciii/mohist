using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class AgentSessionSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"session-spec-runner-{Guid.NewGuid():N}";

    public AgentSessionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_SessionApisExposeTranscript()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("transcript", title: "Build session management");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello from agent\n" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var sessions = await _client.GetDataAsync<WorkflowAgentSessionSummaryDto[]>($"/api/issues/{issue.Number}/coder-sessions?projectId={project.Id}");
        Assert.Contains(sessions, s => s.Id == session.Id && s.SessionName == session.SessionName && s.Status == "completed");

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        Assert.Equal(session.Id, detail.Id);
        Assert.Contains("hello from agent", JsonSerializer.Serialize(detail.Turns));

        var current = await _client.GetDataAsync<WorkflowAgentSessionInfoDto[]>($"/api/agent/sessions?projectId={project.Id}");
        Assert.Contains(current, s => s.SessionId == session.Id && s.IssueTitle == issue.Title);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.Equal(issue.Number, card.IssueNumber);
        Assert.Equal(issue.Title, card.IssueTitle);
        Assert.Equal("completed", card.Status);
        Assert.Equal("hello from agent\n", card.LastActivity?.Text);
        Assert.Equal("text", card.LastActivity?.Kind);
        Assert.Equal(1, activity.Summary.Completed);
        Assert.Equal(0, activity.Summary.Active);
    }

    [Fact]
    public async Task RunnerExecutesAgentWork_ContentTextPayload_AppearsInTranscriptAndActivityPreview()
    {
        var (project, issue, _, session) = await CreateStartedAgentSessionAsync("content-text", title: "Content text payload");
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_message_chunk", payload = new { content = new { type = "text", text = "nested content message\n" }, messageId = "msg-1" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        Assert.Contains("nested content message", JsonSerializer.Serialize(detail.Turns));

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.Equal("nested content message\n", card.LastActivity?.Text);
    }

    [Fact]
    public async Task IssueWorkflowSessionApi_UsesCurrentWorkflowRunAndSessionName()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("current-workflow", sessionName: "plan");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>($"{project.Id}:{issue.Number}");
        await issueGrain.StartWorkAsync();
        await PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "old workflow transcript");

        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSessionId = GrainKey.WorkflowAgentSession(project.Id, currentWorkflowRunId, "plan");
        var currentSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(currentSessionId)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(project.Id, issue.Number, currentWorkflowRunId, "plan", _runnerId, work.WorkId, work.WorkType, work.Stage, "Current plan"));
        await PostEventEntriesAsync(project.Id, currentSession.WorkflowRunId, currentSession.SessionName, "current workflow transcript");

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/workflow/sessions/plan?projectId={project.Id}");

        Assert.Equal(currentSession.Id, detail.Id);
        Assert.Equal("plan", detail.SessionName);
        Assert.Contains("current workflow transcript", JsonSerializer.Serialize(detail.Turns));
        Assert.DoesNotContain("old workflow transcript", JsonSerializer.Serialize(detail.Turns));
    }

    [Fact]
    public async Task WorkflowAgentSessionGrain_ForAgentWork_CreatesDeterministicSessionAndKeepsPollIdempotent()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("idempotent", start: false);
        Assert.Equal(GrainKey.WorkflowAgentSession(work.Issue!.ProjectId, work.WorkflowRunId, work.WorkId), session.Id);

        var repeated = await _fixture.Grains
            .GetGrain<IWorkflowAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(repeated);
        Assert.Equal(session.Id, repeated.Id);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentBatches_AssignsMonotonicSequences()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("sequence");

        await Task.WhenAll(
            PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "first"),
            PostEventEntriesAsync(project.Id, session.WorkflowRunId, session.SessionName, "second"));

        var detail = await _client.GetDataAsync<WorkflowAgentSessionTranscript>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var sequences = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToArrayAsync();
        Assert.Equal([1L, 2L], sequences);
        Assert.Contains("first", JsonSerializer.Serialize(detail.Turns));
        Assert.Contains("second", JsonSerializer.Serialize(detail.Turns));
    }

    [Fact]
    public async Task RunnerReportsTerminalSession_TerminalStatusExists_IgnoresLaterStatusChanges()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("terminal-lock");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_liveness_status", payload = new { status = "probing", failureReason = "late" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "failed", failureReason = "late-failure", exitCode = 1 } }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("completed", grainSession.Status);
        Assert.Equal(0, grainSession.ExitCode);
        Assert.Null(grainSession.FailureReason);
    }

    [Fact]
    public async Task WorkflowAgentSessionEnsure_TerminalSessionExists_ReopensSameSessionForRetry()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-reuse");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "failed", failureReason = "first attempt", exitCode = 1 } }
            }
        });

        var retryRunnerId = $"{_runnerId}-retry";
        var grain = _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id);
        var reopened = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(
            project.Id,
            session.IssueNumber,
            session.WorkflowRunId,
            session.SessionName,
            retryRunnerId,
            work.WorkId,
            work.WorkType,
            work.Stage,
            work.Title));

        Assert.Equal(session.Id, reopened.Id);
        Assert.Equal("failed", reopened.Status);
        Assert.Equal(retryRunnerId, reopened.RunnerId);

        var nextRunnerId = $"{_runnerId}-next";
        var repeated = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(
            project.Id,
            session.IssueNumber,
            session.WorkflowRunId,
            session.SessionName,
            nextRunnerId,
            work.WorkId,
            work.WorkType,
            work.Stage,
            work.Title));

        Assert.Equal(session.Id, repeated.Id);
        Assert.Equal("failed", repeated.Status);
        Assert.Equal(nextRunnerId, repeated.RunnerId);
    }

    [Fact]
    public async Task RunnerUnregisters_WorkInFlight_FailsRunningSession()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-unregister");

        await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).FailIfRunningAsync("Runner unregistered");

        var grainSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Contains("unregistered", grainSession.FailureReason);
    }

    [Fact]
    public async Task EnsureWorkflowAgentSession_TerminalSessionExists_KeepsTerminalSessionClosed()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-terminal");

        await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id).FailIfRunningAsync("Session liveness probe timed out");

        var ensured = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(
                project.Id,
                work.Issue!.IssueNumber,
                work.WorkflowRunId,
                session.SessionName,
                _runnerId,
                work.WorkId,
                work.WorkType,
                work.Stage,
                work.Title));

        Assert.Equal(session.Id, ensured.Id);
        Assert.Equal("failed", ensured.Status);
        Assert.Contains("liveness", ensured.FailureReason);
    }

    [Fact]
    public async Task EnsureWorkflowAgentSession_NamedTerminalSessionStartsNewWork()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("named-reuse", sessionName: "check");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/events", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            events = new[]
            {
                new { type = "agent_session_terminal", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var ensured = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(session.Id)
            .EnsureAsync(new EnsureWorkflowAgentSessionCommand(
                project.Id,
                issue.Number,
                work.WorkflowRunId,
                session.SessionName,
                _runnerId,
                "fix-review-findings:1.1",
                "task",
                "check",
                "Fix review findings"));

        Assert.Equal(session.Id, ensured.Id);
        Assert.Equal("created", ensured.Status);
        Assert.Equal("fix-review-findings:1.1", ensured.WorkId);
        Assert.Null(ensured.CompletedAt);
        Assert.Null(ensured.FailureReason);
    }

    [Fact]
    public async Task AgentActivity_WhenLeaseOwnerDiffers_ReportsOnlyLeaseOwnedActiveSession()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("lease-owner-activity");
        var staleRunnerId = $"stale-runner-{Guid.NewGuid():N}";

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            var row = await db.WorkflowAgentSessions.SingleAsync(s => s.Id == session.Id);
            row.RunnerId = staleRunnerId;
            await db.SaveChangesAsync();
        }

        await SaveLeaseAsync(work.WorkflowRunId, new WorkLease(work.WorkId, work.WorkType, work.Stage ?? "Build", work.WorkId, work.Title, _runnerId));

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");

        Assert.Equal(0, activity.Summary.Active);
        Assert.DoesNotContain(activity.Sessions, s => s.SessionId == session.Id && s.Status is "created" or "running" or "probing");
        Assert.DoesNotContain(activity.Sessions, s => s.IssueNumber == issue.Number && s.Status is "created" or "running" or "probing");
    }

    [Fact(Skip = "Requires design decision: report-failed should close session, but current RunnerGrain.ReportAsync does not propagate to session")]
    public async Task RunnerReport_WhenAgentWorkFailsBeforeTelemetry_ClosesCreatedSession()
    {
        var projectName = $"session-report-failure-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Report closes failed session", body = "track report failure", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), projectId = project.Id });
        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}", new { });
        var work = await PollUntilAgentWorkAsync(issue.Number);

        var sessionName = work.WorkId;
        var sessionId = GrainKey.WorkflowAgentSession(project.Id, work.WorkflowRunId, sessionName);
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{work.WorkflowRunId}/{sessionName}/ensure", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title = work.Title,
            issueNumber = issue.Number,
        });

        await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
        {
            workId = work.WorkId,
            status = "failed",
            projectId = project.Id,
            message = "ACP agent requires 'prompt'",
            exitCode = 1
        });

        var grainSession = await _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(sessionId).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Equal("ACP agent requires 'prompt'", grainSession.FailureReason);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/agent/activity?projectId={project.Id}");
        Assert.Equal(0, activity.Summary.Active);
        Assert.Equal(1, activity.Summary.Failed);
        Assert.Contains(activity.Sessions, s => s.IssueNumber == issue.Number && s.Status == "failed");
    }

    private async Task<WorkDispatchDto> PollUntilAgentWorkAsync(int? expectedIssueNumber = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                await Task.Delay(20);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var work = await response.Content.ReadFromJsonAsync<WorkDispatchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty work dispatch");

            if (work.WorkType == "task" && work.Uses == "mohist/openspec-tasks")
            {
                var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
                await workflow.AddTasksAsync(new AddTasksBatchRequest([
                    new AddTasksBatchItem("build-1", "Build task", "mohist/acp-agent")
                ]));
                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
                {
                    workId = work.WorkId,
                    status = "completed",
                    projectId = work.ProjectId
                });
                continue;
            }

            if (work.Uses == "mohist/acp-agent")
            {
                if (expectedIssueNumber is null || work.IssueNumber == expectedIssueNumber)
                    return work;

                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, status = "completed", projectId = work.ProjectId });
                continue;
            }

            await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, status = "completed", projectId = work.ProjectId });
        }

        Assert.Fail("No agent work dispatched");
        return default!;
    }

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, WorkflowAgentSessionInfo Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"session-grain-{name}-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issueTitle = title ?? $"Session grain {name}";
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = issueTitle, body = "track sessions", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });

        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: issueTitle,
            Issue: new WorkIssueRef(project.Id, issue.Number.ToString(), issue.Number));
        sessionName ??= work.WorkId;
        var grain = _fixture.Grains.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(project.Id, work.WorkflowRunId, sessionName));
        var session = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(project.Id, issue.Number, work.WorkflowRunId, sessionName, _runnerId, work.WorkId, work.WorkType, work.Stage, work.Title));
        if (start)
            await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{project.Id}/{session.WorkflowRunId}/{session.SessionName}/attach", new { agentSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        return (project, issue, work, session);
    }

    private async Task SaveLeaseAsync(string workflowRunId, WorkLease lease)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.WorkflowLeases.FindAsync(workflowRunId);
        var json = JsonSerializer.Serialize(lease, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (row is null)
            db.WorkflowLeases.Add(new Mohist.Server.Workflow.Storage.WorkflowLeaseRow { WorkflowRunId = workflowRunId, StateJson = json });
        else
            row.StateJson = json;
        await db.SaveChangesAsync();
    }

    private Task PostEventEntriesAsync(string projectId, string workflowRunId, string sessionName, string text) => _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{projectId}/{workflowRunId}/{sessionName}/events", new
    {
        events = new[]
        {
            new { type = "agent_message_chunk", payload = new { text } }
        }
    });

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, string? ProjectId, string? IssueId, int? IssueNumber);
    private sealed record WorkflowAgentSessionSummaryDto(string Id, string SessionName, string Status);
    private sealed record WorkflowAgentSessionTranscript(string Id, string SessionName, JsonElement Turns);
    private sealed record WorkflowAgentSessionInfoDto(string SessionId, string IssueTitle, string Status, string? AgentSessionId, string? FailureReason);
    private sealed record ActivityDto(ActivitySummaryDto Summary, ActivityCardDto[] Sessions, ActivityWaitingDto[] Waiting);
    private sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, ActivitySlotUsageDto Slots);
    private sealed record ActivitySlotUsageDto(int Active, int Max);
    private sealed record ActivityCardDto(int IssueNumber, string IssueTitle, string SessionId, string Status, ActivityPreviewDto? LastActivity);
    private sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
    private sealed record ActivityWaitingDto(int IssueNumber, string IssueTitle, string Label);
}
