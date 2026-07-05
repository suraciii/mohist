using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Transcript-based reductions shared by the core
/// <see cref="AgentSessionQuerier"/> and <see cref="AgentActivityFeedAssembler"/>
/// read-side paths (issue-370 T-003 / design D4). Houses the two reductions
/// formerly declared as <c>internal static</c> members on
/// <see cref="AgentSessionQuerier"/> — event-summary batch computation and
/// active-session reconciliation — together with their private helpers.
/// </summary>
/// <remarks>
/// Both reductions depend on <see cref="TranscriptPartLoader.LoadAsync"/> for
/// raw transcript material, <see cref="AgentSessionDtoMapper.ToProjection"/>
/// for projecting parts, <see cref="TranscriptEventSummaryProjector"/> for
/// per-session summarisation, and <see cref="AgentSessionRecord.Label"/>
/// (record-instance fallback) for reading workflow-run / work-id labels
/// during reconciliation. The reductions take <see cref="MohistDbContext"/>
/// as a parameter (not via constructor injection) so they stay pure and
/// match the <see cref="IssueRowMapper"/> / <see cref="AgentSessionDtoMapper"/>
/// static-mapper precedent; callers own the DB context lifetime.
/// </remarks>
internal static class TranscriptReductions
{
    /// <summary>
    /// Loads the transcript parts for the requested session ids, projects
    /// them through <see cref="AgentSessionDtoMapper.ToProjection"/> in
    /// sequence order, groups them by session id, and produces a
    /// session-id → <see cref="AgentSessionTranscriptSummary"/> dictionary.
    /// </summary>
    /// <remarks>
    /// Returns an empty dictionary when the input has no session ids (no
    /// SQL is issued). Sessions that have no transcript turns or parts are
    /// absent from the result — callers observe their absence as "no
    /// summary yet" and project accordingly. Each summary is computed by
    /// <see cref="TranscriptEventSummaryProjector.Summarize"/> over the
    /// session's projected events ordered by <c>(sequence, id)</c>. The
    /// dictionary keying uses <see cref="StringComparer.Ordinal"/> so
    /// callers can index by session id with no case-mapping (issue-327
    /// T-003 / T-002, issue-370 T-003 / design D4).
    /// </remarks>
    internal static async Task<Dictionary<string, AgentSessionTranscriptSummary>> LoadEventSummariesAsync(
        MohistDbContext db, IEnumerable<string> sessionIds, CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, sessionIds, ct: ct);
        if (loaded.Parts.Count == 0) return [];

        return loaded.Parts
            .Where(part => loaded.SessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => AgentSessionDtoMapper.ToProjection(loaded.SessionByTurnId[part.TurnId], part))
            .OrderBy(e => e.Sequence)
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => TranscriptEventSummaryProjector.Summarize(
                g.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson))), StringComparer.Ordinal);
    }

    /// <summary>
    /// Reconciles the input <paramref name="sessions"/> list against the
    /// workflow-run state read from the database. Among the input sessions,
    /// only those bound to a runner (<see cref="AgentSessionRow.AgentSessionId"/>
    /// is not null) are candidates for filtering; each candidate is
    /// validated against its workflow run by single-runner assignment and
    /// running-task work-id match, and sessions whose workflow run is
    /// absent or whose assignment has not yet been recorded are
    /// provisionally accepted. Non-active sessions and accepted active
    /// sessions pass through unchanged. The result ordering and
    /// membership match the pre-relocation semantics.
    /// </summary>
    internal static async Task<IReadOnlyList<AgentSessionRecord>> ReconcileActiveSessionsAsync(
        MohistDbContext db,
        IReadOnlyList<AgentSessionRecord> sessions,
        CancellationToken ct)
    {
        if (sessions.Count == 0) return sessions;

        var activeRows = sessions
            .Where(IsActiveSession)
            .ToList();
        if (activeRows.Count == 0) return sessions;

        var runsByWorkflow = await LoadWorkflowRunsForReconciliationAsync(db, activeRows, ct);
        if (runsByWorkflow.Count == 0) return sessions;

        var allowedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activeSession in activeRows)
        {
            var workflowRunId = activeSession.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
            if (workflowRunId is null || !runsByWorkflow.TryGetValue(workflowRunId, out var run) || run is null)
            {
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (IsSessionAssociatedWithRun(run, activeSession))
                allowedSessionIds.Add(activeSession.Session.Id);
        }

        return sessions
            .Where(s => !IsActiveSession(s) || allowedSessionIds.Contains(s.Session.Id))
            .ToList();
    }

    private static Mohist.Server.Workflow.Domain.Run.WorkflowRun? DeserializeWorkflowRun(string json)
    {
        try { return JsonSerializer.Deserialize<Mohist.Server.Workflow.Domain.Run.WorkflowRun>(json, Infrastructure.Data.Sessions.AgentSessionJson.JsonOptions); }
        catch { return null; }
    }

    private static async Task<Dictionary<string, Mohist.Server.Workflow.Domain.Run.WorkflowRun?>> LoadWorkflowRunsForReconciliationAsync(
        MohistDbContext db, List<AgentSessionRecord> sessions, CancellationToken ct)
    {
        var workflowIds = sessions
            .Select(s => s.Label(AgentSessionQueryMetadataKeys.WorkflowRunId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(r => workflowIds.Contains(r.WorkflowRunId))
            .ToListAsync(ct);

        var runs = new Dictionary<string, Mohist.Server.Workflow.Domain.Run.WorkflowRun?>(StringComparer.Ordinal);
        foreach (var row in rows)
            runs[row.WorkflowRunId] = DeserializeWorkflowRun(row.State);
        return runs;
    }

    /// <summary>
    /// Determines whether <paramref name="session"/> is associated with <paramref name="run"/>
    /// by reference through a <see cref="Mohist.Server.Workflow.Domain.Run.TaskRun"/>, not by ownership.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentSession"/> is a peer aggregate, never owned by <see cref="Mohist.Server.Workflow.Domain.Run.WorkflowRun"/>.
    /// The association is established through a <see cref="Mohist.Server.Workflow.Domain.Run.TaskRun"/> session reference and
    /// relies on the single-runner assignment invariant: if the run is assigned, the session MUST
    /// belong to the same runner (<see cref="WorkflowAssignmentInfo.RunnerId"/> == session.RunnerId)
    /// and the task identified by <see cref="AgentSessionQueryMetadataKeys.WorkId"/> (if running)
    /// MUST match the session's work item (the task whose reference links them).
    /// When the run has no assignment yet (<see cref="Mohist.Server.Workflow.Domain.Run.WorkflowRun.AssignedTo"/> is null), any active
    /// session known by workflow-run-id is provisionally accepted.
    /// </remarks>
    private static bool IsSessionAssociatedWithRun(Mohist.Server.Workflow.Domain.Run.WorkflowRun run, AgentSessionRecord session)
    {
        if (run.AssignedTo is null) return true;

        if (!string.Equals(run.AssignedTo, session.Row.RunnerId, StringComparison.Ordinal))
            return false;

        var runningTask = run.Stages
            .SelectMany(s => s.Tasks)
            .FirstOrDefault(t => t.Status == Workflow.Domain.Run.TaskRunStatus.Running);

        return runningTask is null || string.Equals(runningTask.Id, session.Label(AgentSessionQueryMetadataKeys.WorkId), StringComparison.Ordinal);
    }

    private static bool IsActiveSession(AgentSessionRecord session) =>
        session.Row.AgentSessionId is not null;
}