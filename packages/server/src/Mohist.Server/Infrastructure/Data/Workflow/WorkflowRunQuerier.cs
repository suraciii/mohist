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
        return JSON.Deserialize<WorkflowRun>(WorkflowRunStore.MigrateAssignmentJson(row.State));
    }

    public async Task<IReadOnlyList<string>> FindAssignedToAsync(string runnerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.AssignedRunnerId == runnerId)
            .OrderBy(row => row.WorkflowRunId)
            .Select(row => row.WorkflowRunId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> FindAssignableAsync(string? projectId = null, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.WorkflowRuns
            .AsNoTracking()
            .Where(row => row.AssignedRunnerId == null);

        if (!string.IsNullOrWhiteSpace(projectId))
            query = query.Where(row => row.MetadataProjectId == projectId);

        var assignable = new List<string>(Math.Max(1, limit));
        var pageSize = Math.Max(20, limit * 4);
        var offset = 0;
        while (assignable.Count < limit)
        {
            var rows = await query
                .OrderBy(row => row.CreatedAt)
                .ThenBy(row => row.WorkflowRunId)
                .Skip(offset)
                .Take(pageSize)
                .ToListAsync(ct);
            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var run = JSON.Deserialize<WorkflowRun>(WorkflowRunStore.MigrateAssignmentJson(row.State));
                if (run is null) continue;
                if (run.Status != WorkflowRunStatus.Running) continue;
                if (run.Assignment is not null) continue;
                if (run.NextWork() is null) continue;

                assignable.Add(row.WorkflowRunId);
                if (assignable.Count >= limit)
                    break;
            }

            offset += rows.Count;
        }

        return assignable;
    }
}
