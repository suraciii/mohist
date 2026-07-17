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
        var run = JSON.Deserialize<WorkflowRun>(WorkflowRunStore.MigrateLegacyWorkflowRunJson(row.State));
        if (run is not null)
            WorkflowRunLineage.RestoreStoredEpicNumber(run, row.EpicNumber);
        return run;
    }

    /// <summary>
    /// Epic #44: returns workflow runs bound to <paramref name="workerId"/>
    /// and sitting in <c>Ready</c> (assigned, dispatchable work, no in-flight
    /// work), ordered by <c>ReadySince ASC</c> for round-robin fairness. A run
    /// records when it (re-)entered Ready; serving the oldest-Ready run first
    /// means a just-served run re-queues at the tail — fairness as a property
    /// of persisted data with zero scheduler state (see
    /// <c>design/workflow/scheduling.md</c> §Fairness). Filters at the DB layer
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

    /// <summary>
    /// Issue-318 D4: returns workflow runs that are unassigned and waiting
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

    /// <summary>
    /// Issue-318 D4: counts workflow runs that are currently in flight
    /// (<c>Running</c>) and bound to <paramref name="workerId"/>. Used by
    /// the runner grain's dispatch-capacity gate so the per-runner slot
    /// budget accounts for work already picked up. Filters at the database
    /// layer on the STORED <c>Status</c> computed column plus
    /// <c>AssignedWorkerId</c>; never deserializes <c>State</c>. Replaces
    /// the previous <c>FindAssignedToAsync</c> +
    /// <c>GetCurrentWorkIdAsync</c> fan-out, which under the new state
    /// machine would have collapsed to zero (Ready excludes in-flight
    /// work).
    /// </summary>
    public async Task<int> CountRunningAssignedToAsync(string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Running) && row.AssignedWorkerId == workerId)
            .Select(row => row.WorkflowRunId)
            .CountAsync(ct);
    }

    /// <summary>
    /// Epic #44: returns the ids of workflow runs currently in flight
    /// (<c>Running</c>) and bound to <paramref name="workerId"/> — the
    /// <c>desired</c> set for poll reconciliation. The DispatchService loads
    /// each to resolve its current workId and render a redelivery dispatch when
    /// the runner's reported set does not include it. Filters at the DB layer;
    /// never deserializes <c>State</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindRunningAssignedToAsync(string workerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.Status == StatusString(WorkflowRunStatus.Running) && row.AssignedWorkerId == workerId)
            .Select(row => row.WorkflowRunId)
            .ToListAsync(ct);
    }

    // The STORED Status computed column is the lowercase JSON enum
    // value (D3). This helper is the single point that knows the
    // SQLite column == lowercase canonical form contract; every
    // status-filtering query funnels through it so a future rename
    // changes one site, not three (or all the read sites).
    private static string StatusString(WorkflowRunStatus status) =>
        status.ToString().ToLowerInvariant();
}
