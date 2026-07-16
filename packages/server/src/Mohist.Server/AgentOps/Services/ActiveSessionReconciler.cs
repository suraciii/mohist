using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.AgentOps.Services;

internal static class ActiveSessionReconciler
{
    private abstract record WorkflowRunState
    {
        internal sealed record Present(WorkflowRun Run) : WorkflowRunState;
        internal sealed record Missing : WorkflowRunState;
        internal sealed record Invalid(string Error) : WorkflowRunState;
    }

    internal static async Task<IReadOnlyList<AgentSessionRecord>> ReconcileAsync(
        MohistDbContext db,
        IReadOnlyList<AgentSessionRecord> sessions,
        ILogger logger,
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
            if (workflowRunId is null)
            {
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (!runsByWorkflow.TryGetValue(workflowRunId, out var state))
            {
                // No persisted row: the run may not yet be recorded. Keep the
                // session (prior behavior) rather than dropping it speculatively.
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (state is WorkflowRunState.Invalid invalid)
            {
                // Fail closed: the persisted workflow state cannot be trusted, so
                // the active session must not be retained against it. Surface the
                // integrity error so operators can repair the run instead of the
                // invalid state being silently hidden as a missing run.
                logger.LogError(
                    "Cannot reconcile active session {SessionId}: workflow run {WorkflowRunId} persisted state is invalid: {Error}",
                    activeSession.Session.Id, workflowRunId, invalid.Error);
                continue;
            }

            if (state is WorkflowRunState.Missing)
            {
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (IsAssociatedWithRun(((WorkflowRunState.Present)state).Run, activeSession))
                allowedSessionIds.Add(activeSession.Session.Id);
        }

        return sessions
            .Where(session => !IsActiveSession(session) || allowedSessionIds.Contains(session.Session.Id))
            .ToList();
    }

    private static async Task<Dictionary<string, WorkflowRunState>> LoadWorkflowRunsAsync(
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

        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var byWorkflow = new Dictionary<string, WorkflowRunState>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            loaded.Add(row.WorkflowRunId);
            byWorkflow[row.WorkflowRunId] = DeserializeWorkflowRun(row.WorkflowRunId, row.State);
        }

        // Ids the session referenced but no row exists for: treat as missing
        // (run not yet recorded), distinct from a row that failed to deserialize.
        foreach (var id in workflowRunIds)
            if (!loaded.Contains(id))
                byWorkflow[id] = new WorkflowRunState.Missing();

        return byWorkflow;
    }

    private static WorkflowRunState DeserializeWorkflowRun(string workflowRunId, string json)
    {
        try
        {
            var run = JsonSerializer.Deserialize<WorkflowRun>(
                WorkflowRunStore.MigrateLegacyWorkflowRunJson(json), AgentSessionJson.JsonOptions);
            return run is null
                ? new WorkflowRunState.Invalid("workflow run deserialized to null")
                : new WorkflowRunState.Present(run);
        }
        catch (Exception ex)
        {
            return new WorkflowRunState.Invalid(ex.Message);
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
