using Microsoft.EntityFrameworkCore;
using Mohist.Server.Events;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Projection;

public class WorkflowProjectionService
{
    private readonly IGrainFactory _grains;
    private readonly IssueQueryService _issues;
    private readonly IEventStore _events;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowProjectionService(IGrainFactory grains, IssueQueryService issues, IEventStore events, IDbContextFactory<MohistDbContext> dbFactory)
    {
        _grains = grains;
        _issues = issues;
        _events = events;
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowTimelineDto?> GetTimelineAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var issue = await _issues.GetAsync(projectId, issueNumber);
        if (issue?.WorkflowRunId is null) return null;

        var workflow = _grains.GetGrain<IWorkflowGrain>(issue.WorkflowRunId);
        var status = await workflow.GetStatusAsync();
        if (status is null) return null;

        var events = await _events.ListWorkflowEventsAsync(issue.WorkflowRunId, 1000, ct);
        var sessions = await ListSessionsAsync(issue.WorkflowRunId, ct);

        return BuildTimeline(status, events, sessions);
    }

    public async Task<IReadOnlyList<ActiveAgentDto>> ListActiveAgentsAsync(string? projectId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.CompletedAt == null && (s.Status == AgentSessionStatus.Created || s.Status == AgentSessionStatus.Running || s.Status == AgentSessionStatus.Probing));
        if (!string.IsNullOrWhiteSpace(projectId)) query = query.Where(s => s.ProjectId == projectId);

        var sessions = await query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var result = new List<ActiveAgentDto>();

        foreach (var session in sessions)
        {
            var workflow = _grains.GetGrain<IWorkflowGrain>(session.WorkflowRunId);
            var status = await workflow.GetStatusAsync();
            var pending = status?.PendingWork;
            if (pending is null || pending.WorkId != session.WorkId) continue;

            var timeline = status is null
                ? null
                : BuildTimeline(status, await _events.ListWorkflowEventsAsync(session.WorkflowRunId, 1000, ct), [session]);
            var currentStage = timeline?.Stages.FirstOrDefault(s => s.Stage == pending.Stage);
            var completed = currentStage?.Tasks.Count(t => t.Status == "completed") ?? 0;
            var total = currentStage?.Tasks.Count ?? 0;
            var lastActivity = (session.LastDataAt ?? session.StartedAt ?? session.CreatedAt).ToString("o");

            result.Add(new ActiveAgentDto(
                session.RunnerId ?? string.Empty,
                $"issue_{session.ProjectId}_{session.IssueNumber}",
                session.IssueNumber,
                session.ProjectId,
                session.WorkflowRunId,
                session.WorkId ?? string.Empty,
                session.WorkType ?? string.Empty,
                session.Stage,
                session.Title,
                session.Id,
                (session.StartedAt ?? session.CreatedAt).ToString("o"),
                lastActivity,
                new ActiveAgentProgressDto(
                    session.Stage,
                    new ActiveWorkItemDto(session.WorkType ?? "task", session.WorkId ?? session.WorkflowRunId, session.Title ?? session.WorkId ?? session.WorkflowRunId),
                    new TaskProgressDto(completed, total),
                    lastActivity)));
        }

        return result;
    }

    private static WorkflowTimelineDto BuildTimeline(WorkflowStatusSnapshot status, IReadOnlyList<EventDto> events, IReadOnlyList<WorkflowAgentSession> sessions)
    {
        var eventList = events.ToList();
        var stages = status.Stages
            .OrderBy(s => s.Order)
            .Select(stage => BuildStage(stage, eventList, sessions))
            .ToList();

        return new WorkflowTimelineDto(
            status.WorkflowRunId,
            status.Status,
            status.CurrentStage,
            status.PendingWork is null ? null : new PendingWorkDto(status.PendingWork.WorkId, status.PendingWork.WorkType, status.PendingWork.Stage, status.PendingWork.Title, status.PendingWork.Uses),
            stages,
            status.AvailableActions.Select(a => new AvailableActionDto(a.Name, a.Label, a.Target)).ToList());
    }

    private static WorkflowStageDto BuildStage(StageStatusSnapshot stage, List<EventDto> events, IReadOnlyList<WorkflowAgentSession> sessions)
    {
        var stageEvents = events.Where(e => e.Stage == stage.Stage).ToList();
        var startedAt = stageEvents.FirstOrDefault()?.CreatedAt;
        var completedAt = IsTerminal(stage.Status) ? stageEvents.LastOrDefault()?.CreatedAt : null;

        var tasks = stage.Tasks.Select(task =>
        {
            var taskSessions = sessions.Where(s => s.Stage == stage.Stage && (s.WorkId == task.Id || s.Title == task.Title)).ToList();
            var taskEvents = stageEvents.Where(e => e.TaskId == task.Id || e.Payload?.ToString()?.Contains(task.Id, StringComparison.OrdinalIgnoreCase) == true).ToList();
            var start = taskSessions.Select(s => s.StartedAt ?? s.CreatedAt).Cast<DateTime?>().Min()?.ToString("o")
                ?? taskEvents.FirstOrDefault()?.CreatedAt;
            var end = taskSessions.Select(s => s.CompletedAt).Where(d => d is not null).Cast<DateTime?>().Max()?.ToString("o")
                ?? (IsTerminal(task.Status) ? taskEvents.LastOrDefault()?.CreatedAt : null);
            var attempts = Math.Max(1, taskSessions.Count == 0 ? taskEvents.Count(e => e.Type == "workflow_task_started") : taskSessions.Count);
            return new WorkflowTaskDto(task.Id, task.Title, task.Uses, NormalizeStatus(task.Status), start, end, DurationMs(start, end), attempts, taskEvents.LastOrDefault(e => e.Message is not null)?.Message);
        }).ToList();

        var checks = stage.Checks.Select(check =>
        {
            var checkEvents = stageEvents.Where(e => e.CheckName == check.Name).ToList();
            var start = checkEvents.FirstOrDefault(e => e.Type == "workflow_check_started")?.CreatedAt ?? checkEvents.FirstOrDefault()?.CreatedAt;
            var end = IsTerminal(check.Status) ? checkEvents.LastOrDefault()?.CreatedAt : null;
            return new WorkflowCheckDto(check.Name, check.Title, check.Uses, NormalizeStatus(check.Status), check.Message, start, end, DurationMs(start, end));
        }).ToList();

        return new WorkflowStageDto(
            stage.Stage,
            NormalizeStatus(stage.Status),
            stage.Order,
            startedAt,
            completedAt,
            DurationMs(startedAt, completedAt),
            tasks,
            checks,
            stage.Approval is null ? null : new ApprovalDto(stage.Approval.Status, stage.Approval.Output, stage.Approval.RequestedAt, stage.Approval.RespondedAt));
    }

    private async Task<IReadOnlyList<WorkflowAgentSession>> ListSessionsAsync(string workflowRunId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    private static string NormalizeStatus(string value) => value.ToLowerInvariant() switch
    {
        "passed" or "pass" or "completed" => "completed",
        "failed" or "fail" => "failed",
        "awaitingapproval" or "awaiting-approval" => "awaiting-approval",
        "running" => "running",
        "pending" => "pending",
        _ => value.ToLowerInvariant(),
    };

    private static bool IsTerminal(string status) => NormalizeStatus(status) is "completed" or "failed";

    private static long? DurationMs(string? start, string? end)
    {
        if (start is null || end is null) return null;
        return DateTime.TryParse(start, out var s) && DateTime.TryParse(end, out var e)
            ? Math.Max(0, (long)(e - s).TotalMilliseconds)
            : null;
    }
}

public sealed record WorkflowTimelineDto(string WorkflowRunId, string Status, string? CurrentStage, PendingWorkDto? PendingWork, IReadOnlyList<WorkflowStageDto> Stages, IReadOnlyList<AvailableActionDto> AvailableActions);
public sealed record WorkflowStageDto(string Stage, string Status, int Order, string? StartedAt, string? CompletedAt, long? DurationMs, IReadOnlyList<WorkflowTaskDto> Tasks, IReadOnlyList<WorkflowCheckDto> Checks, ApprovalDto? Approval);
public sealed record WorkflowTaskDto(string Id, string Title, string? Uses, string Status, string? StartedAt, string? CompletedAt, long? DurationMs, int Attempts, string? Message);
public sealed record WorkflowCheckDto(string Name, string Title, string? Uses, string Status, string? Message, string? StartedAt, string? CompletedAt, long? DurationMs);
public sealed record ApprovalDto(string Status, string? Output, string RequestedAt, string? RespondedAt);
public sealed record PendingWorkDto(string WorkId, string WorkType, string? Stage, string? Title, string? Uses);
public sealed record AvailableActionDto(string Name, string Label, string? Target);

public sealed record ActiveAgentDto(string RunnerId, string IssueId, int IssueNumber, string ProjectId, string WorkflowRunId, string WorkId, string WorkType, string? Stage, string? Title, string SessionId, string StartedAt, string LastActivityAt, ActiveAgentProgressDto Progress);
public sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
public sealed record ActiveWorkItemDto(string Type, string Id, string Title);
public sealed record TaskProgressDto(int Completed, int Total);
