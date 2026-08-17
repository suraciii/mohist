using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public sealed class WorkflowRunQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Loads the persisted <see cref="WorkflowRun"/> state for the given
    /// run id. Used by execution-plane callers (e.g. RunnerGrain's
    /// translator) that need a read-only projection without crossing the
    /// control-plane grain boundary. Returns <c>null</c> if the row does
    /// not exist; callers fall back to grain state when projection lag
    /// is unacceptable.
    /// </summary>
    public async Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId, ct);
        if (row is null) return null;
        var run = JSON.Deserialize<WorkflowRun>(row.State);
        if (run is not null)
            WorkflowRunLineage.RestoreStoredEpicNumber(run, row.EpicNumber);
        return run;
    }

    public async Task<WorkflowRunRoutingContext?> LoadRoutingContextAsync(
        string workflowRunId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId, ct);
        if (row is null)
            return null;

        var run = JSON.Deserialize<WorkflowRun>(row.State);
        if (run is null)
            return null;

        WorkflowRunLineage.RestoreStoredEpicNumber(run, row.EpicNumber);
        return new WorkflowRunRoutingContext(
            run.Id,
            row.MetadataProjectId,
            row.IssueNumber,
            row.EpicNumber,
            run.Workspace?.Path,
            run.Status.ToString(),
            run.Status.IsTerminal());
    }

    /// <summary>
    /// Epic #44: returns workflow runs bound to <paramref name="workerId"/>
    /// and sitting in <c>Ready</c> (assigned, dispatchable work, no in-flight
    /// work), ordered by <c>ReadySince ASC</c> for round-robin fairness. A run
    /// records when it (re-)entered Ready; serving the oldest-Ready run first
    /// means a just-served run re-queues at the tail — fairness as a property
    /// of persisted data with zero scheduler state. Filters at the DB layer
    /// on the STORED <c>Status</c> column + <c>AssignedWorkerId</c>, backed by
    /// <c>IX_WorkflowRuns_Status_ReadySince</c>; never deserializes
    /// <c>State</c>. The <c>Ready</c> filter already excludes in-flight work,
    /// so every row returned is directly pickup-able.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindAssignedToAsync(string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Ready) && row.AssignedWorkerId == workerId)
            .OrderBy(row => row.ReadySince)
            .ThenBy(row => row.WorkflowRunId)
            .Select(row => row.WorkflowRunId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowRunScheduleCandidate>> FindAssignedCandidatesAsync(
        string workerId,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Ready) && row.AssignedWorkerId == workerId)
            .OrderBy(row => row.ReadySince)
            .ThenBy(row => row.WorkflowRunId)
            .Take(limit)
            .Select(row => new { row.WorkflowRunId, row.ReadySince, row.CreatedAt })
            .ToListAsync(ct);

        return rows.Select(row => new WorkflowRunScheduleCandidate(
            row.WorkflowRunId,
            ToUtc(row.ReadySince ?? row.CreatedAt ?? DateTime.UnixEpoch))).ToList();
    }

    /// <summary>
    /// returns workflow runs that are unassigned and waiting
    /// for *any* runner to claim (<c>Pending</c>). Filters at the database
    /// layer on the STORED <c>Status</c> computed column; never
    /// deserializes the <c>State</c> JSON of non-matching rows. The
    /// unassigned (<c>AssignedWorkerId IS NULL</c>) is implied by the
    /// <c>Pending</c> status under the new state machine (D1) and is
    /// asserted redundantly here as defensive belt-and-suspenders against
    /// any orphan row that survives the migration without an assignment.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindAssignableAsync(string? projectId = null, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Pending) && row.AssignedWorkerId == null);

        if (!string.IsNullOrWhiteSpace(projectId))
            query = query.Where(row => row.MetadataProjectId == projectId);

        return await query
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.WorkflowRunId)
            .Take(limit)
            .Select(row => row.WorkflowRunId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowRunScheduleCandidate>> FindAssignableCandidatesAsync(
        string? projectId = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Pending) && row.AssignedWorkerId == null);

        if (!string.IsNullOrWhiteSpace(projectId))
            query = query.Where(row => row.MetadataProjectId == projectId);

        var rows = await query
            .OrderBy(row => row.ReadySince)
            .ThenBy(row => row.CreatedAt)
            .ThenBy(row => row.WorkflowRunId)
            .Take(limit)
            .Select(row => new { row.WorkflowRunId, row.ReadySince, row.CreatedAt })
            .ToListAsync(ct);

        return rows.Select(row => new WorkflowRunScheduleCandidate(
            row.WorkflowRunId,
            ToUtc(row.ReadySince ?? row.CreatedAt ?? DateTime.UnixEpoch))).ToList();
    }

    /// <summary>
    /// counts workflow runs that are currently in flight
    /// (<c>Running</c>) and bound to <paramref name="workerId"/>. Used by
    /// the runner grain's dispatch-capacity gate so the per-runner slot
    /// budget accounts for work already picked up. Filters at the database
    /// layer on the STORED <c>Status</c> computed column, the assigned owner,
    /// the materialized active-work projection, and the durable blocked
    /// attention projection; never deserializes <c>State</c>. Replaces
    /// the previous <c>FindAssignedToAsync</c> +
    /// <c>GetCurrentWorkIdAsync</c> fan-out, which under the new state
    /// machine would have collapsed to zero (Ready excludes in-flight
    /// work).
    ///
    /// Issue-628 T-005: durably <c>Blocked</c> Agent settlements are
    /// excluded from this count. The durable <c>WorkflowRunRow.AttentionStatus
    /// = "blocked"</c> projection and the materialized active-work
    /// projection (<c>ActiveWorkId</c>/<c>ActiveWorkerId</c>) are the
    /// release boundary for Runner control-plane capacity: a single
    /// boundary shared with <see cref="FindRunningAssignedToAsync"/>. A
    /// pre-deadline <c>Unknown</c> attempt still counts because its row is
    /// not yet marked blocked and still carries its active-work projection;
    /// the same row is released exactly once the workflow commits the
    /// <c>Unknown</c> → <c>Blocked</c> transition and clears the projection.
    /// </summary>
    public async Task<int> CountRunningAssignedToAsync(string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Running)
                && row.AssignedWorkerId == workerId
                && row.ActiveWorkId != null
                && row.ActiveWorkerId == workerId
                && row.AttentionStatus != BlockedAttentionStatus)
            .Select(row => row.WorkflowRunId)
            .CountAsync(ct);
    }

    /// <summary>
    /// Epic #44: returns the ids of workflow runs currently in flight
    /// (<c>Running</c>) and bound to <paramref name="workerId"/> — the
    /// <c>desired</c> set for poll reconciliation. The DispatchService loads
    /// each to resolve its current workId and render a redelivery dispatch when
    /// the runner's reported set does not include it. Only rows with a real
    /// active-work projection are returned, so blocked settlement rows cannot
    /// retain a redelivery slot. Filters at the DB layer; never deserializes
    /// <c>State</c>.
    ///
    /// Issue-628 T-005: durably <c>Blocked</c> Agent settlements are
    /// excluded from the desired set. The same release boundary that
    /// decrements the capacity count in <see cref="CountRunningAssignedToAsync"/>
    /// (the materialized active-work projection plus the durable blocked
    /// attention projection) also drops the run from
    /// <c>DispatchService.AddMissingRedeliveriesAsync</c> and from the Runner
    /// runtime <c>activeWorks</c> projection — none of these three
    /// control-plane surfaces is allowed to re-release the same row on a
    /// subsequent reminder, poll, or status read.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindRunningAssignedToAsync(string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Running)
                && row.AssignedWorkerId == workerId
                && row.ActiveWorkId != null
                && row.ActiveWorkerId == workerId
                && row.AttentionStatus != BlockedAttentionStatus)
            .Select(row => row.WorkflowRunId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns nonterminal runs whose authoritative Agent settlement reached
    /// blocked. The indexed row projection is rebuilt with the WorkflowRun and
    /// intentionally does not become a second state-machine authority.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindBlockedAsync(
        string? projectId = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.AttentionStatus == BlockedAttentionStatus);

        if (!string.IsNullOrWhiteSpace(projectId))
            query = query.Where(row => row.MetadataProjectId == projectId);

        return await query
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.WorkflowRunId)
            .Take(limit)
            .Select(row => row.WorkflowRunId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Single point of truth for the durable Agent-blocked attention
    /// projection. Every control-plane query that excludes a durably
    /// blocked run funnels through this constant so a future rename of
    /// the projection column does not require touching multiple sites
    /// (the same pattern as <see cref="StatusString"/>).
    /// </summary>
    private const string BlockedAttentionStatus = "blocked";

    // The STORED Status computed column is the lowercase JSON enum
    // value (D3). This helper is the single point that knows the
    // SQLite column == lowercase canonical form contract; every
    // status-filtering query funnels through it so a future rename
    // changes one site, not three (or all the read sites).
    private static string StatusString(WorkflowRunStatus status) =>
        status.ToString().ToLowerInvariant();

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed record WorkflowRunScheduleCandidate(string WorkflowRunId, DateTimeOffset ReadySince);

public sealed record WorkflowRunRoutingContext(
    string WorkflowRunId,
    string? ProjectId,
    int? IssueNumber,
    int? EpicNumber,
    string? WorkspacePath,
    string Status,
    bool IsTerminal);
