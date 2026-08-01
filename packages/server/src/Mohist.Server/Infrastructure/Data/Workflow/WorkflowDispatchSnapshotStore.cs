using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Workflow;

// Persistence boundary for dispatch snapshots. Stores and returns the raw
// snapshot JSON only — it deliberately does not know the WorkDispatch shape
// (an Application-layer grain contract), keeping Infrastructure.Data free of
// Application dependencies. Callers in the Application layer serialize and
// deserialize.
public interface IDispatchSnapshotStore
{
    Task<string?> LoadJsonAsync(string workflowRunId, string workId, CancellationToken ct = default);

    Task<string> SaveFirstJsonAsync(
        string workflowRunId,
        string workId,
        string snapshotJson,
        CancellationToken ct = default);

    Task DeleteAsync(string workflowRunId, string workId, CancellationToken ct = default);

    Task DeleteForRunAsync(string workflowRunId, CancellationToken ct = default);
}

public sealed class DispatchSnapshotStore : IDispatchSnapshotStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<DispatchSnapshotStore> _log;

    public DispatchSnapshotStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<DispatchSnapshotStore> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    public async Task<string?> LoadJsonAsync(string workflowRunId, string workId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct);
        return row?.SnapshotJson;
    }

    public async Task<string> SaveFirstJsonAsync(
        string workflowRunId,
        string workId,
        string snapshotJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workflowRunId and workId must be non-empty");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct);
        if (existing is not null)
            return existing.SnapshotJson;

        var newRow = new WorkflowDispatchSnapshotRow
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            SnapshotJson = snapshotJson,
        };
        db.WorkflowDispatchSnapshots.Add(newRow);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent writer inserted the same key first (race between grains
            // or a retry after grain reactivation). Honor first-write-wins by
            // reloading the existing row.
            _log.LogDebug(ex,
                "DispatchSnapshotStore concurrent insert for workflow {WorkflowRunId} work {WorkId}; reloading winner",
                workflowRunId, workId);
            await db.Entry(newRow).ReloadAsync(ct);
            var winner = await db.WorkflowDispatchSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct)
                ?? throw new InvalidOperationException(
                    $"DispatchSnapshotStore could not persist or load snapshot for workflow {workflowRunId} work {workId}");
            return winner.SnapshotJson;
        }
        return snapshotJson;
    }

    public async Task DeleteAsync(string workflowRunId, string workId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct);
        if (row is null)
            return;
        db.WorkflowDispatchSnapshots.Remove(new WorkflowDispatchSnapshotRow
        {
            WorkflowRunId = row.WorkflowRunId,
            WorkId = row.WorkId,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already removed by a concurrent writer; first-write-wins for
            // deletes is irrelevant — the row is gone either way.
        }
    }

    public async Task DeleteForRunAsync(string workflowRunId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.WorkflowDispatchSnapshots
            .Where(s => s.WorkflowRunId == workflowRunId)
            .ExecuteDeleteAsync(ct);
    }
}
