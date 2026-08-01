using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public interface IDispatchSnapshotStore
{
    Task<WorkDispatch?> LoadAsync(string workflowRunId, string workId, CancellationToken ct = default);

    Task<WorkDispatch> SaveFirstAsync(string workflowRunId, string workId, WorkDispatch dispatch, CancellationToken ct = default);

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

    public async Task<WorkDispatch?> LoadAsync(string workflowRunId, string workId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct);
        if (row is null)
            return null;
        try
        {
            return JSON.Deserialize<WorkDispatch>(row.SnapshotJson);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "DispatchSnapshotStore failed to deserialize snapshot for workflow {WorkflowRunId} work {WorkId}",
                workflowRunId, workId);
            return null;
        }
    }

    public async Task<WorkDispatch> SaveFirstAsync(
        string workflowRunId,
        string workId,
        WorkDispatch dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workflowRunId and workId must be non-empty");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct);
        if (existing is not null)
        {
            try
            {
                return JSON.Deserialize<WorkDispatch>(existing.SnapshotJson) ?? dispatch;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "DispatchSnapshotStore failed to deserialize existing snapshot for workflow {WorkflowRunId} work {WorkId}; falling back to caller payload",
                    workflowRunId, workId);
                return dispatch;
            }
        }

        var newRow = new WorkflowDispatchSnapshotRow
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            SnapshotJson = JSON.Serialize(dispatch),
        };
        db.WorkflowDispatchSnapshots.Add(newRow);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent writer inserted the same key first (race between grains
            // or a retry after grain reactivation). Honor first-write-wins by
            // reloading the existing row.
            await db.Entry(newRow).ReloadAsync(ct);
            var winner = await db.WorkflowDispatchSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.WorkId == workId, ct);
            if (winner is null)
                throw;
            try
            {
                return JSON.Deserialize<WorkDispatch>(winner.SnapshotJson) ?? dispatch;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "DispatchSnapshotStore failed to deserialize winner snapshot for workflow {WorkflowRunId} work {WorkId}; falling back to caller payload",
                    workflowRunId, workId);
                return dispatch;
            }
        }
        return dispatch;
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
