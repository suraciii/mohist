using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services;

public class WorkflowActivityQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQuerier _workflowQuerier;

    public WorkflowActivityQuerier(IDbContextFactory<MohistDbContext> dbFactory, WorkflowQuerier workflowQuerier)
    {
        _dbFactory = dbFactory;
        _workflowQuerier = workflowQuerier;
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

            var status = await _workflowQuerier.GetStatusAsync(session.WorkflowRunId);
            var pending = status?.PendingWork;
            if (status is null || pending is null || pending.WorkId != session.WorkId) continue;

            var currentStage = status.Stages.FirstOrDefault(s => s.Stage == pending.Stage);
            var completed = currentStage?.Tasks.Count(t => string.Equals(t.Status, TaskRunStatus.Completed.ToString(), StringComparison.Ordinal)) ?? 0;
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

    private static async Task<Dictionary<string, WorkLease?>> LoadLeasesAsync(MohistDbContext db, string[] workflowIds, CancellationToken ct)
    {
        if (workflowIds.Length == 0) return [];

        var rows = await db.WorkflowLeases.AsNoTracking()
            .Where(row => workflowIds.Contains(row.WorkflowRunId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.WorkflowRunId, row => WorkflowLeaseJson.Deserialize(row.State), StringComparer.Ordinal);
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

public sealed record ActiveAgentDto(string RunnerId, string IssueId, int IssueNumber, string ProjectId, string WorkflowRunId, string WorkId, string WorkType, string? Stage, string? Title, string SessionId, string StartedAt, string LastActivityAt, ActiveAgentProgressDto Progress);
public sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
public sealed record ActiveWorkItemDto(string Type, string Id, string Title);
public sealed record TaskProgressDto(int Completed, int Total);
