using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.AgentOps.Services;

internal static class ActiveSessionReconciler
{
    internal static async Task<IReadOnlyList<AgentSessionRecord>> ReconcileAsync(
        MohistDbContext db,
        IReadOnlyList<AgentSessionRecord> sessions,
        CancellationToken ct)
    {
        if (sessions.Count == 0) return sessions;

        var activeSessions = sessions
            .Where(IsActiveSession)
            .ToList();
        if (activeSessions.Count == 0) return sessions;

        var runsByWorkflow = await LoadWorkflowRunsAsync(db, activeSessions, ct);
        if (runsByWorkflow.Count == 0) return sessions;

        var allowedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activeSession in activeSessions)
        {
            var workflowRunId = activeSession.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
            if (workflowRunId is null || !runsByWorkflow.TryGetValue(workflowRunId, out var run) || run is null)
            {
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (IsAssociatedWithRun(run, activeSession))
                allowedSessionIds.Add(activeSession.Session.Id);
        }

        return sessions
            .Where(session => !IsActiveSession(session) || allowedSessionIds.Contains(session.Session.Id))
            .ToList();
    }

    private static async Task<Dictionary<string, WorkflowRun?>> LoadWorkflowRunsAsync(
        MohistDbContext db,
        IReadOnlyList<AgentSessionRecord> sessions,
        CancellationToken ct)
    {
        var workflowRunIds = sessions
            .Select(session => session.Label(AgentSessionQueryMetadataKeys.WorkflowRunId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(row => workflowRunIds.Contains(row.WorkflowRunId))
            .ToListAsync(ct);

        return rows.ToDictionary(
            row => row.WorkflowRunId,
            row => DeserializeWorkflowRun(row.State),
            StringComparer.Ordinal);
    }

    private static WorkflowRun? DeserializeWorkflowRun(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowRun>(json, AgentSessionJson.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAssociatedWithRun(WorkflowRun run, AgentSessionRecord session)
    {
        if (run.AssignedTo is null) return true;
        if (!string.Equals(run.AssignedTo, session.Row.RunnerId, StringComparison.Ordinal)) return false;

        var runningTask = run.Stages
            .SelectMany(stage => stage.Tasks)
            .FirstOrDefault(task => task.Status == TaskRunStatus.Running);

        return runningTask is null
            || string.Equals(runningTask.Id, session.Label(AgentSessionQueryMetadataKeys.WorkId), StringComparison.Ordinal);
    }

    private static bool IsActiveSession(AgentSessionRecord session) =>
        session.Row.AgentSessionId is not null;
}
