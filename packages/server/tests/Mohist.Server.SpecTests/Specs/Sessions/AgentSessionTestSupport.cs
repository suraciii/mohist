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

public abstract class AgentSessionTestSupport
{
    protected readonly HttpClient _client;
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly string _runnerId = $"session-spec-runner-{Guid.NewGuid():N}";

    protected AgentSessionTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    protected async Task<WorkDispatchDto> PollUntilAgentWorkAsync(int? expectedIssueNumber = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            var work = await response.ReadFirstDispatchAsync<WorkDispatchDto>();
            if (work is null)
                continue;

            if (work.WorkType == "task" && work.Uses == "mohist/openspec-tasks")
            {
                var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
                await workflow.AddTasksAsync(new AddTasksBatchRequest([
                    new AddTasksBatchItem("build-1", "Build task", "mohist/opencode")
                ]));
                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new
                {
                    workId = work.WorkId,
                    workflowRunId = work.WorkflowRunId,
                    status = "completed",
                    projectId = work.ProjectId
                });
                continue;
            }

            if (work.Uses == "mohist/opencode")
            {
                if (expectedIssueNumber is null || work.IssueNumber == expectedIssueNumber)
                    return work;

                await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, workflowRunId = work.WorkflowRunId, status = "completed", projectId = work.ProjectId });
                continue;
            }

            await _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId = work.WorkId, workflowRunId = work.WorkflowRunId, status = "completed", projectId = work.ProjectId });
        }

        Assert.Fail("No agent work dispatched");
        return default!;
    }

    protected async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateStartedAgentSessionAsync(string name, bool start = true, string? title = null, string? sessionName = null)
    {
        var projectName = $"asg-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issueTitle = title ?? $"Session grain {name}";
var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = issueTitle, body = "track sessions", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });

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

    protected string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    protected string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    protected string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

    protected async Task<CreatedSession> OpenRunnerSessionAsync(string projectId, int issueNumber, string workflowRunId, string sessionName, WorkDispatch work, string title)
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

    protected async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    protected async Task<string> AcceptSessionRuntimeEventTurnAsync(CreatedSession session)
    {
        var receipt = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).AcceptFollowupAsync(
            new AcceptFollowupCommand("record runtime events", "test", $"runtime-events-{session.SessionName}"));
        return receipt.TurnId;
    }

    protected Task PostSessionTurnRuntimeEventsAsync(
        CreatedSession session,
        string turnId,
        params (string Type, object Payload)[] runtimeEvents) =>
        _client.PostOkAsync($"/api/runner/{_runnerId}/agent-sessions/{session.Id}/runtime-events", new
        {
            runtimeSessionId = session.Id,
            agentSessionId = session.Id,
            agentTurnId = turnId,
            runtimeEvents = runtimeEvents.Select(runtimeEvent => new
            {
                type = runtimeEvent.Type,
                payload = WithTurnId(runtimeEvent.Payload, turnId)
            }).ToArray()
        });

    protected Task PostEventEntriesAsync(CreatedSession session, string turnId, string text) =>
        PostSessionTurnRuntimeEventsAsync(session, turnId, ("message.delta", new { text }));

    private static JsonElement WithTurnId(object payload, string turnId)
    {
        var properties = JsonSerializer.SerializeToElement(payload)
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        properties["turnId"] = JsonSerializer.SerializeToElement(turnId);
        return JsonSerializer.SerializeToElement(properties);
    }

    protected static async Task<AgentSessionTranscriptPartRow[]> LoadTranscriptPartsAsync(MohistDbContext db, string sessionId)
    {
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Select(e => e.Id)
            .ToArrayAsync();

        return await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToArrayAsync();
    }

    protected static async Task SeedOutOfOrderTranscriptPartsAsync(IDbContextFactory<MohistDbContext> dbFactory, string sessionId)
    {
        var baseTime = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        await using var db = await dbFactory.CreateDbContextAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = baseTime,
            UpdatedAt = baseTime.AddMinutes(5),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.AddRange(
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 20,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "metadata-model-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "sequence-last-model" }),
                LastSeenAt = baseTime.AddMinutes(20),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 10,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "metadata-model-inserted-last",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "inserted-last-model" }),
                LastSeenAt = baseTime.AddMinutes(10),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 30,
                Type = TranscriptPartTypes.SessionActivity,
                CorrelationKey = "metadata-closed-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "sequence-last-failure" }),
                LastSeenAt = baseTime.AddMinutes(30),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 15,
                Type = TranscriptPartTypes.SessionActivity,
                CorrelationKey = "metadata-closed-inserted-last",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "inserted-last-failure" }),
                LastSeenAt = baseTime.AddMinutes(15),
            });
        await db.SaveChangesAsync();
    }

    protected static AgentSessionMetadata WorkflowSessionMetadata(
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

    protected sealed record ProjectDto(string Id, string Name);
    protected sealed record IssueDto(string Id, int Number, string Title);
    protected sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }

    protected sealed record WorkDispatchDto(string WorkflowRunId, string WorkId, string? Uses, string? With, string WorkType, string? Stage, string? Title, string? ProjectId, string? IssueId, int? IssueNumber);
    protected sealed record AgentSessionSummaryDto(
        string Id,
        string SessionName,
        [property: JsonPropertyName("activity")] string Status,
        [property: JsonPropertyName("lastDataAt")] string? LastDataAt);
    protected sealed record ActivityDto(ActivitySummaryDto Summary, ActivityCardDto[] Sessions, ActivityWaitingDto[] Waiting);
    protected sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, ActivitySlotUsageDto Slots);
    protected sealed record ActivitySlotUsageDto(int Active, int Max);
    protected sealed record ActivityCardDto(
        int IssueNumber,
        string IssueTitle,
        string SessionId,
        string Status,
        ActivityPreviewDto? LastActivity,
        AgentEventSummaryDto? EventSummary,
        AgentUsageDto? Usage,
        ActivityTaskProgressDto? TaskProgress);
    protected sealed record AgentEventSummaryDto(
        string? ResolvedModel,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount);
    protected sealed record AgentUsageDto(
        long? InputTokens,
        long? OutputTokens,
        long? TotalTokens,
        long? CachedReadTokens,
        long? ThoughtTokens,
        double? CostAmount,
        string? CostCurrency,
        long? ContextWindowUsed,
        long? ContextWindowSize);
    protected sealed record ActivityTaskProgressDto(int Completed, int Total);
    protected sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
    protected sealed record ActivityWaitingDto(int IssueNumber, string IssueTitle, string Label);
    protected sealed record AgentSessionTranscriptTestResponse(AgentSessionTranscriptTurnTestDto[] Turns, int PartCount, string? LastActivityAt);
    protected sealed record AgentSessionTranscriptTurnTestDto(string Id, AgentSessionTranscriptUserTestDto User, AgentSessionTranscriptPartTestDto[] Assistant, string StartedAt, string? CompletedAt, bool Incomplete);
    protected sealed record AgentSessionTranscriptUserTestDto(string Role, string Text, string Kind, string SentAt);
    protected sealed record AgentSessionTranscriptPartTestDto(string Id, string Type, string? Text, string? Message, string? Kind, AgentSessionTranscriptToolTestDto? Tool);
    protected sealed record AgentSessionTranscriptToolTestDto(string ToolCallId, string ToolName, string Status, string? Title);
}
