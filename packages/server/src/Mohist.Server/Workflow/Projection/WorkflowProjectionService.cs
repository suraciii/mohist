using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Views;
using Mohist.Server.Workflow.Queries;

namespace Mohist.Server.Workflow.Projection;

public class WorkflowProjectionService
{
    private readonly IssueQueryService _issues;
    private readonly IEventStore _events;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQueryService _workflowReader;

    public WorkflowProjectionService(IssueQueryService issues, IEventStore events, IDbContextFactory<MohistDbContext> dbFactory, WorkflowQueryService workflowReader)
    {
        _issues = issues;
        _events = events;
        _dbFactory = dbFactory;
        _workflowReader = workflowReader;
    }

    public async Task<WorkflowTimelineDto?> GetTimelineAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var issue = await _issues.GetAsync(projectId, issueNumber);
        if (issue?.WorkflowRunId is null) return null;

        var status = await _workflowReader.GetStatusAsync(issue.WorkflowRunId);
        if (status is null) return null;

        var events = await _events.ListWorkflowEventsAsync(issue.WorkflowRunId, 1000, ct);
        var sessions = await ListSessionsAsync(issue.WorkflowRunId, ct);

        return BuildTimeline(status, events, sessions);
    }

    public async Task<IReadOnlyList<ActiveAgentDto>> ListActiveAgentsAsync(string? projectId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.CompletedAt == null && (s.Status == "created" || s.Status == "running" || s.Status == "probing"));
        if (!string.IsNullOrWhiteSpace(projectId)) query = query.Where(s => s.ProjectId == projectId);

        var sessions = await query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var leases = await LoadLeasesAsync(db, sessions.Select(s => s.WorkflowRunId).Distinct(StringComparer.Ordinal).ToArray(), ct);
        var domainSessions = sessions.Select(ToDomain).ToList();
        var result = new List<ActiveAgentDto>();

        foreach (var session in domainSessions)
        {
            if (!IsLeaseOwnedActiveSession(session, leases)) continue;

            var status = await _workflowReader.GetStatusAsync(session.WorkflowRunId);
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

    private static WorkflowTimelineDto BuildTimeline(WorkflowStatusView status, IReadOnlyList<EventDto> events, IReadOnlyList<WorkflowAgentSession> sessions)
    {
        var eventList = events.ToList();
        var stages = status.Stages
            .Select((stage, i) => BuildStage(stage, i, eventList, sessions))
            .ToList();

        return new WorkflowTimelineDto(
            status.WorkflowRunId,
            status.Status,
            status.CurrentStage,
            status.PendingWork,
            stages,
            status.AvailableActions);
    }

    private static WorkflowStageDto BuildStage(StageStatusView stage, int order, List<EventDto> events, IReadOnlyList<WorkflowAgentSession> sessions)
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
            return new WorkflowTaskDto(task.Id, task.Title, task.Uses, NormalizeStatus(task.Status), start, end, DurationMs(start, end), attempts, taskEvents.LastOrDefault(e => e.Message is not null)?.Message, task.RequiredFiles, task.Classification);
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
            order,
            startedAt,
            completedAt,
            DurationMs(startedAt, completedAt),
            tasks,
            checks,
            stage.ApprovalStatus is null ? null : new ApprovalDto(stage.ApprovalStatus.Result, stage.ApprovalStatus.RequestedAt, stage.ApprovalStatus.RespondedAt));
    }

    private async Task<IReadOnlyList<WorkflowAgentSession>> ListSessionsAsync(string workflowRunId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    private static WorkflowAgentSession ToDomain(WorkflowAgentSessionRow r) => new()
    {
        Id = r.Id,
        ProjectId = r.ProjectId,
        IssueNumber = r.IssueNumber,
        WorkflowRunId = r.WorkflowRunId,
        SessionName = r.SessionName,
        WorkId = r.WorkId,
        WorkType = r.WorkType,
        Stage = r.Stage,
        Title = r.Title,
        RunnerId = r.RunnerId,
        AgentSessionId = r.AgentSessionId,
        Status = AgentSessionStatusNames.Parse(r.Status),
        Model = r.Model,
        WorkDir = r.WorkDir,
        ChangeDir = r.ChangeDir,
        ProcessPid = r.ProcessPid,
        CreatedAt = r.CreatedAt,
        StartedAt = r.StartedAt,
        LastDataAt = r.LastDataAt,
        LastHeartbeatAt = r.LastHeartbeatAt,
        CompletedAt = r.CompletedAt,
        FailureReason = r.FailureReason,
        ExitCode = r.ExitCode,
    };

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

    private static async Task<Dictionary<string, WorkLease?>> LoadLeasesAsync(MohistDbContext db, string[] workflowIds, CancellationToken ct)
    {
        if (workflowIds.Length == 0) return [];

        var rows = await db.WorkflowQueue.AsNoTracking()
            .Where(row => workflowIds.Contains(row.WorkflowRunId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.WorkflowRunId, QueueLease, StringComparer.Ordinal);
    }

    private static WorkLease? QueueLease(WorkflowQueueRow row)
    {
        if (row.State != WorkflowQueueStates.Leased
            || string.IsNullOrWhiteSpace(row.WorkId)
            || string.IsNullOrWhiteSpace(row.WorkType)
            || string.IsNullOrWhiteSpace(row.Stage)
            || string.IsNullOrWhiteSpace(row.LogicalId))
            return null;

        return new WorkLease(row.WorkId, row.WorkType, row.Stage, row.LogicalId, row.Title, row.RunnerId);
    }

    private static bool IsLeaseOwnedActiveSession(WorkflowAgentSession session, IReadOnlyDictionary<string, WorkLease?> leases)
    {
        if (session.CompletedAt is not null || session.Status is not (AgentSessionStatus.Created or AgentSessionStatus.Running or AgentSessionStatus.Probing))
            return false;

        if (!leases.TryGetValue(session.WorkflowRunId, out var lease) || lease is null)
            return true;

        return string.Equals(session.RunnerId, lease.RunnerId, StringComparison.Ordinal)
            && string.Equals(session.WorkId, lease.WorkId, StringComparison.Ordinal);
    }
}

public sealed record WorkflowTimelineDto(string WorkflowRunId, string Status, string? CurrentStage, PendingWorkView? PendingWork, IReadOnlyList<WorkflowStageDto> Stages, IReadOnlyList<AvailableActionView> AvailableActions);
public sealed record WorkflowStageDto(string Stage, string Status, int Order, string? StartedAt, string? CompletedAt, long? DurationMs, IReadOnlyList<WorkflowTaskDto> Tasks, IReadOnlyList<WorkflowCheckDto> Checks, ApprovalDto? ApprovalStatus);
public sealed record WorkflowTaskDto(string Id, string Title, string? Uses, string Status, string? StartedAt, string? CompletedAt, long? DurationMs, int Attempts, string? Message, IReadOnlyList<WorkflowTaskRequiredFile>? RequiredFiles, TaskClassification Classification);
public sealed record WorkflowCheckDto(string Name, string Title, string? Uses, string Status, string? Message, string? StartedAt, string? CompletedAt, long? DurationMs);
public sealed record ApprovalDto(string? Result, string RequestedAt, string? RespondedAt);

public sealed record ActiveAgentDto(string RunnerId, string IssueId, int IssueNumber, string ProjectId, string WorkflowRunId, string WorkId, string WorkType, string? Stage, string? Title, string SessionId, string StartedAt, string LastActivityAt, ActiveAgentProgressDto Progress);
public sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
public sealed record ActiveWorkItemDto(string Type, string Id, string Title);
public sealed record TaskProgressDto(int Completed, int Total);
