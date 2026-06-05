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
        var query = db.AgentSessions.AsNoTracking()
            .Where(s => s.CompletedAt == null && (s.Status == "created" || s.Status == "running" || s.Status == "probing"));
        if (!string.IsNullOrWhiteSpace(projectId)) query = query.Where(s => s.ProjectId == projectId);

        var sessions = await query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var leases = await LoadLeasesAsync(db, sessions.Select(s => s.WorkflowRunId).Distinct(StringComparer.Ordinal).ToArray(), ct);
        var result = new List<ActiveAgentDto>();

        foreach (var row in sessions)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null) continue;
            if (!IsLeaseOwnedActiveSession(session, row, leases)) continue;

            var status = await _workflowQuerier.GetStatusAsync(session.RunId);
            var pending = status?.PendingWork;
            if (status is null || pending is null || pending.WorkId != row.WorkId) continue;

            var currentStage = status.Stages.FirstOrDefault(s => s.Stage == pending.Stage);
            var completed = currentStage?.Tasks.Count(t => string.Equals(t.Status, TaskRunStatus.Completed.ToString(), StringComparison.Ordinal)) ?? 0;
            var total = currentStage?.Tasks.Count ?? 0;
            var lastActivity = LastActivityAt(session).ToString("o");

            result.Add(new ActiveAgentDto(
                session.Runtime.RunnerId,
                $"issue_{session.ProjectId}_{session.IssueNumber}",
                session.IssueNumber,
                session.ProjectId,
                session.RunId,
                row.WorkId ?? string.Empty,
                row.WorkType ?? string.Empty,
                row.Stage,
                null,
                session.Id,
                (session.Status.StartedAt ?? session.Status.CreatedAt).ToString("o"),
                lastActivity,
                new ActiveAgentProgressDto(
                    row.Stage,
                    new ActiveWorkItemDto(row.WorkType ?? "task", row.WorkId ?? session.RunId, row.WorkId ?? session.RunId),
                    new TaskProgressDto(completed, total),
                    lastActivity)));
        }

        return result;
    }

    private static async Task<Dictionary<string, WorkLease?>> LoadLeasesAsync(MohistDbContext db, string[] workflowIds, CancellationToken ct)
    {
        if (workflowIds.Length == 0) return [];

        var rows = await db.WorkflowLeases.AsNoTracking()
            .Where(row => workflowIds.Contains(row.WorkflowRunId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.WorkflowRunId, row => WorkflowLeaseJson.Deserialize(row.State), StringComparer.Ordinal);
    }

    private static bool IsLeaseOwnedActiveSession(AgentSession session, AgentSessionRow row, IReadOnlyDictionary<string, WorkLease?> leases)
    {
        if (session.Status.CompletedAt is not null || session.Status.Phase is not (AgentSessionStatus.Created or AgentSessionStatus.Running or AgentSessionStatus.Probing))
            return false;

        if (!leases.TryGetValue(session.RunId, out var lease) || lease is null)
            return true;

        return string.Equals(session.Runtime.RunnerId, lease.RunnerId, StringComparison.Ordinal)
            && string.Equals(row.WorkId, lease.WorkId, StringComparison.Ordinal);
    }

    private static DateTime LastActivityAt(AgentSession session) =>
        session.Status.LastDataAt ?? session.Status.StartedAt ?? session.Status.CreatedAt;
}

public sealed record ActiveAgentDto(string RunnerId, string IssueId, int IssueNumber, string ProjectId, string WorkflowRunId, string WorkId, string WorkType, string? Stage, string? Title, string SessionId, string StartedAt, string LastActivityAt, ActiveAgentProgressDto Progress);
public sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
public sealed record ActiveWorkItemDto(string Type, string Id, string Title);
public sealed record TaskProgressDto(int Completed, int Total);
