using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Storage.Db;
using Mohist.Server.Tests.Support;
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
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/started", new { externalSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/events", new
        {
            events = new[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello from agent\n" } }
            }
        });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/completed", new { status = "completed", exitCode = 0 });

        var sessions = await _client.GetDataAsync<CoderSessionSummaryDto[]>($"/api/issues/{issue.Number}/coder-sessions?projectId={project.Id}");
        Assert.Contains(sessions, s => s.Id == session.Id && s.Status == "completed");

        var detail = await _client.GetDataAsync<CoderSessionDetailDto>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        Assert.Equal(session.Id, detail.Id);
        Assert.Contains("hello from agent", JsonSerializer.Serialize(detail.Turns));

        var current = await _client.GetDataAsync<AgentSessionInfoDto[]>($"/api/agent/sessions?projectId={project.Id}");
        Assert.Contains(current, s => s.SessionId == session.Id);

        var activity = await _client.GetDataAsync<AgentActivityDto>($"/api/agent/activity?projectId={project.Id}");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);
        Assert.Equal(issue.Number, card.IssueNumber);
        Assert.Equal("Build session management", card.IssueTitle);
        Assert.Equal("completed", card.Status);
        Assert.Equal("hello from agent\n", card.LastActivity?.Text);
        Assert.Equal("text", card.LastActivity?.Kind);
        Assert.Equal(1, activity.Summary.Completed);
        Assert.Equal(0, activity.Summary.Active);
    }

    [Fact]
    public async Task AgentSessionGrain_ForAgentWork_CreatesDeterministicSessionAndKeepsPollIdempotent()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("idempotent", start: false);
        Assert.Equal(AgentSessionGrainKeys.ForWork(work.WorkflowRunId, work.WorkId), session.Id);

        var repeated = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(repeated);
        Assert.Equal(session.Id, repeated.Id);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentBatches_AssignsMonotonicSequences()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("sequence");

        await Task.WhenAll(
            PostEventsAsync(session.Id, "first"),
            PostEventsAsync(session.Id, "second"));

        var detail = await _client.GetDataAsync<CoderSessionDetailDto>($"/api/issues/{issue.Number}/coder-sessions/{session.Id}?projectId={project.Id}");
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var sequences = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToArrayAsync();
        Assert.Equal([1L, 2L], sequences);
        Assert.Contains("first", JsonSerializer.Serialize(detail.Turns));
        Assert.Contains("second", JsonSerializer.Serialize(detail.Turns));
    }

    [Fact]
    public async Task RunnerReportsTerminalSession_TerminalStateExists_IgnoresLaterStatusChanges()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("terminal-lock");

        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/completed", new { status = "completed", exitCode = 0 });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/status", new { status = "probing", failureReason = "late" });
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/completed", new { status = "failed", failureReason = "late-failure", exitCode = 1 });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("completed", grainSession.Status);
        Assert.Equal(0, grainSession.ExitCode);
        Assert.Null(grainSession.FailureReason);
    }

    [Fact]
    public async Task RunnerUnregisters_WorkInFlight_FailsRunningAgentSession()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-unregister");

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).FailIfRunningAsync("Runner unregistered");

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("failed", grainSession.Status);
        Assert.Contains("unregistered", grainSession.FailureReason);
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

            if (work.WorkType == "load")
            {
                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
                {
                    workId = work.WorkId,
                    status = "loaded",
                    output = JsonSerializer.Serialize(new { tasks = new[] { new { id = "build-1", title = "Build task", uses = "mohist/acp-agent" } } })
                });
                continue;
            }

            if (work.Uses == "mohist/acp-agent")
            {
                if (expectedIssueNumber is null || work.IssueNumber == expectedIssueNumber)
                    return work;

                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, status = "completed" });
                continue;
            }

            await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, status = "completed" });
        }

        Assert.Fail("No agent work dispatched");
        return default!;
    }

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, AgentSessionSnapshot Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null)
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
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(AgentSessionGrainKeys.ForWork(work.WorkflowRunId, work.WorkId));
        var session = await grain.EnsureCreatedAsync(new EnsureAgentSessionCommand(_runnerId, work));
        Assert.NotNull(session);
        if (start)
            await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/started", new { externalSessionId = session.Id, workDir = project.Path, processPid = 1234 });
        return (project, issue, work, session);
    }

    private Task PostEventsAsync(string sessionId, string text) => _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{sessionId}/events", new
    {
        events = new[]
        {
            new { type = "agent_message_chunk", payload = new { text } }
        }
    });

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record AgentSessionDispatchDto(string Id, string ProjectId, int IssueNumber, string WorkflowRunId, string WorkId, string? Stage, string? Title, string? ExternalSessionId);
    private sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, string? ProjectId, string? IssueId, int? IssueNumber, AgentSessionDispatchDto? Session);
    private sealed record CoderSessionSummaryDto(string Id, string Status);
    private sealed record CoderSessionDetailDto(string Id, JsonElement Turns, WorkflowLogItemDto[] WorkflowLogs);
    private sealed record WorkflowLogItemDto(string Id, string EventType, JsonElement Data, string CreatedAt);
    private sealed record AgentSessionInfoDto(string SessionId);
    private sealed record AgentStatusDto(ActiveAgentDto[] ActiveAgents);
    private sealed record ActiveAgentDto(int IssueNumber, string WorkId, string? Stage, ActiveAgentProgressDto Progress);
    private sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
    private sealed record ActiveWorkItemDto(string Type, string Id, string Title);
    private sealed record TaskProgressDto(int Completed, int Total);
    private sealed record AgentActivityDto(AgentActivitySummaryDto Summary, AgentActivitySessionDto[] Sessions, AgentActivityWaitingDto[] Waiting);
    private sealed record AgentActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, AgentActivitySlotUsageDto Slots);
    private sealed record AgentActivitySlotUsageDto(int Active, int Max);
    private sealed record AgentActivitySessionDto(int IssueNumber, string IssueTitle, string SessionId, string Status, AgentActivityPreviewDto? LastActivity);
    private sealed record AgentActivityPreviewDto(string Kind, string Text, string CreatedAt);
    private sealed record AgentActivityWaitingDto(int IssueNumber, string IssueTitle, string Label);
}
