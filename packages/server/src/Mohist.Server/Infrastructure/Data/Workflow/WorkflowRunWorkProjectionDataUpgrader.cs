using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public sealed record WorkflowRunWorkProjectionUpgradeResult(
    int CandidateCount,
    int WrittenCount);

public static class WorkflowRunWorkProjectionDataUpgrader
{
    private const int BatchSize = 500;

    public static async Task<WorkflowRunWorkProjectionUpgradeResult> UpgradeAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        var rows = await db.WorkflowRuns
            .AsNoTracking()
            .OrderBy(row => row.WorkflowRunId)
            .Select(row => new SourceRow(
                row.WorkflowRunId,
                row.State,
                row.ActiveWorkId,
                row.ActiveWorkerId))
            .ToListAsync(cancellationToken);
        var mapRows = await db.WorkflowRunTaskMaps
            .AsNoTracking()
            .OrderBy(row => row.WorkflowRunId)
            .ThenBy(row => row.TaskId)
            .ToListAsync(cancellationToken);
        var mapsByRun = mapRows
            .GroupBy(row => row.WorkflowRunId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var upgrades = new List<PreparedUpgrade>();
        var diagnostics = new List<string>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var run = JSON.Deserialize<WorkflowRun>(row.State)
                    ?? throw new InvalidOperationException("deserialized to null");
                if (!string.Equals(run.Id, row.WorkflowRunId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"state id is '{run.Id}', expected '{row.WorkflowRunId}'");

                var projection = WorkflowRunWorkProjectionBuilder.Build(run);
                mapsByRun.TryGetValue(row.WorkflowRunId, out var currentMaps);
                if (!Matches(row, currentMaps ?? [], projection))
                    upgrades.Add(new PreparedUpgrade(row.WorkflowRunId, projection));
            }
            catch (Exception exception)
            {
                diagnostics.Add($"WorkflowRun '{row.WorkflowRunId}': {exception.Message}");
            }
        }

        if (diagnostics.Count > 0)
        {
            logger?.LogError(
                "WorkflowRun work projection preflight failed: rowCount={RowCount}, candidateCount={CandidateCount}, failureCount={FailureCount}",
                rows.Count,
                upgrades.Count,
                diagnostics.Count);
            throw new InvalidOperationException(
                "WorkflowRun work projection preflight failed:\n"
                + string.Join("\n", diagnostics));
        }

        logger?.LogInformation(
            "WorkflowRun work projection preflight completed: rowCount={RowCount}, candidateCount={CandidateCount}",
            rows.Count,
            upgrades.Count);

        if (upgrades.Count == 0)
            return new WorkflowRunWorkProjectionUpgradeResult(0, 0);

        var upgradesById = upgrades.ToDictionary(upgrade => upgrade.WorkflowRunId, StringComparer.Ordinal);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var trackedRows = new List<WorkflowRunRow>(upgrades.Count);
            foreach (var ids in upgrades.Select(upgrade => upgrade.WorkflowRunId).Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                trackedRows.AddRange(await db.WorkflowRuns
                    .Where(row => ids.Contains(row.WorkflowRunId))
                    .ToListAsync(cancellationToken));
            }
            if (trackedRows.Count != upgrades.Count)
                throw new InvalidOperationException("WorkflowRun work projection lost a candidate row before write");

            var existingMaps = new List<WorkflowRunTaskMapRow>();
            foreach (var ids in upgrades.Select(upgrade => upgrade.WorkflowRunId).Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                existingMaps.AddRange(await db.WorkflowRunTaskMaps
                    .Where(row => ids.Contains(row.WorkflowRunId))
                    .ToListAsync(cancellationToken));
            }
            var existingMapsByRun = existingMaps
                .GroupBy(row => row.WorkflowRunId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            foreach (var row in trackedRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projection = upgradesById[row.WorkflowRunId].Projection;
                row.ActiveWorkId = projection.ActiveWorkId;
                row.ActiveWorkerId = projection.ActiveWorkerId;
                ApplyTaskMap(db, projection, existingMapsByRun.GetValueOrDefault(row.WorkflowRunId) ?? []);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        logger?.LogInformation(
            "WorkflowRun work projection committed: writtenCount={WrittenCount}",
            upgrades.Count);
        return new WorkflowRunWorkProjectionUpgradeResult(upgrades.Count, upgrades.Count);
    }

    private static void ApplyTaskMap(
        MohistDbContext db,
        WorkflowRunWorkProjectionData projection,
        IReadOnlyList<WorkflowRunTaskMapRow> existing)
    {
        var expected = projection.TaskMap.ToDictionary(entry => entry.TaskId, StringComparer.Ordinal);
        foreach (var row in existing)
        {
            if (expected.Remove(row.TaskId, out var entry))
                row.WorkId = entry.WorkId;
            else
                db.WorkflowRunTaskMaps.Remove(row);
        }

        db.WorkflowRunTaskMaps.AddRange(expected.Values.Select(entry => new WorkflowRunTaskMapRow
        {
            WorkflowRunId = projection.WorkflowRunId,
            TaskId = entry.TaskId,
            WorkId = entry.WorkId,
        }));
    }

    private static bool Matches(
        SourceRow row,
        IReadOnlyList<WorkflowRunTaskMapRow> currentMaps,
        WorkflowRunWorkProjectionData projection)
    {
        if (!string.Equals(row.ActiveWorkId, projection.ActiveWorkId, StringComparison.Ordinal)
            || !string.Equals(row.ActiveWorkerId, projection.ActiveWorkerId, StringComparison.Ordinal))
            return false;

        var expected = projection.TaskMap.ToDictionary(entry => entry.TaskId, StringComparer.Ordinal);
        return currentMaps.Count == expected.Count
            && currentMaps.All(map => expected.TryGetValue(map.TaskId, out var entry)
                && string.Equals(map.WorkId, entry.WorkId, StringComparison.Ordinal));
    }

    private sealed record SourceRow(
        string WorkflowRunId,
        string State,
        string? ActiveWorkId,
        string? ActiveWorkerId);

    private sealed record PreparedUpgrade(
        string WorkflowRunId,
        WorkflowRunWorkProjectionData Projection);
}
