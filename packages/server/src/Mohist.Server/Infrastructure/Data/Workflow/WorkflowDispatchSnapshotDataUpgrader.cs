using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public sealed record WorkflowDispatchSnapshotUpgradeResult(
    int CandidateCount,
    int WrittenCount,
    int ExternalizedCount,
    int SweptOrphanCount,
    string? BackupPath);

public static class WorkflowDispatchSnapshotDataUpgrader
{
    private const string DispatchSnapshot = "dispatchSnapshot";

    public static async Task<WorkflowDispatchSnapshotUpgradeResult> ExternalizeAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default,
        Func<SqliteConnection, CancellationToken, Task<string>>? backup = null,
        ILogger? logger = null)
    {
        var rows = await db.WorkflowRuns
            .AsNoTracking()
            .OrderBy(row => row.WorkflowRunId)
            .ToListAsync(cancellationToken);
        var upgrades = new List<(string WorkflowRunId, string State)>();
        var externalized = new Dictionary<(string WorkflowRunId, string WorkId), string>();
        var diagnostics = new List<string>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rowSnapshots = new List<(string WorkflowRunId, string WorkId, string SnapshotJson)>();
                var stripped = StripDispatchSnapshots(row.State, row.WorkflowRunId, rowSnapshots, out var changed);
                if (JSON.Deserialize<WorkflowRun>(stripped) is null)
                    throw new InvalidOperationException("deserialized to null");

                foreach (var snapshot in rowSnapshots)
                {
                    var key = (snapshot.WorkflowRunId, snapshot.WorkId);
                    if (externalized.TryGetValue(key, out var existing)
                        && !string.Equals(existing, snapshot.SnapshotJson, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"multiple dispatch snapshots have the same WorkId '{snapshot.WorkId}'");
                    }
                    externalized[key] = snapshot.SnapshotJson;
                }

                if (changed)
                    upgrades.Add((row.WorkflowRunId, stripped));
            }
            catch (Exception exception)
            {
                diagnostics.Add($"WorkflowRun '{row.WorkflowRunId}': {exception.Message}");
            }
        }

        if (diagnostics.Count > 0)
        {
            logger?.LogError(
                "DispatchSnapshot externalization preflight failed: candidateCount={CandidateCount}, failureCount={FailureCount}",
                upgrades.Count,
                diagnostics.Count);
            throw new InvalidOperationException(
                "DispatchSnapshot externalization preflight failed:\n"
                + string.Join("\n", diagnostics));
        }

        logger?.LogInformation(
            "DispatchSnapshot externalization preflight completed: candidateCount={CandidateCount}, externalizedCount={ExternalizedCount}",
            upgrades.Count,
            externalized.Count);

        if (upgrades.Count == 0)
            return new WorkflowDispatchSnapshotUpgradeResult(0, 0, 0, 0, null);

        var source = db.Database.GetDbConnection() as SqliteConnection
            ?? throw new InvalidOperationException("DispatchSnapshot externalization requires SQLite");
        var sourceWasOpen = source.State == System.Data.ConnectionState.Open;
        string backupPath;
        try
        {
            if (!sourceWasOpen)
                await source.OpenAsync(cancellationToken);
            backupPath = backup is null
                ? await WorkflowRunStateDataUpgrader.CreateAndVerifyBackupAsync(source, cancellationToken)
                : await backup(source, cancellationToken);
        }
        finally
        {
            if (!sourceWasOpen)
                await source.CloseAsync();
        }

        var insertedCount = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var states = upgrades.ToDictionary(
                upgrade => upgrade.WorkflowRunId,
                upgrade => upgrade.State,
                StringComparer.Ordinal);
            var trackedRows = new List<WorkflowRunRow>(upgrades.Count);
            foreach (var ids in upgrades.Select(upgrade => upgrade.WorkflowRunId).Chunk(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                trackedRows.AddRange(await db.WorkflowRuns
                    .Where(row => ids.Contains(row.WorkflowRunId))
                    .ToListAsync(cancellationToken));
            }
            if (trackedRows.Count != upgrades.Count)
                throw new InvalidOperationException("DispatchSnapshot externalization lost a candidate row before write");

            foreach (var row in trackedRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row.State = states[row.WorkflowRunId];
                var etag = db.Entry(row).Property<long>("ETag");
                etag.CurrentValue = etag.OriginalValue + 1;
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var snapshot in externalized)
            {
                insertedCount += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT OR IGNORE INTO "WorkflowDispatchSnapshots" ("WorkflowRunId", "WorkId", "SnapshotJson")
                    VALUES ({snapshot.Key.WorkflowRunId}, {snapshot.Key.WorkId}, {snapshot.Value});
                    """, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        logger?.LogInformation(
            "DispatchSnapshot externalization committed: writtenCount={WrittenCount}, externalizedCount={ExternalizedCount}, backupPath={BackupPath}",
            upgrades.Count,
            insertedCount,
            backupPath);
        return new WorkflowDispatchSnapshotUpgradeResult(upgrades.Count, upgrades.Count, insertedCount, 0, backupPath);
    }

    public static async Task<int> SweepOrphansAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        var snapshots = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .OrderBy(snapshot => snapshot.WorkflowRunId)
            .ThenBy(snapshot => snapshot.WorkId)
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0)
            return 0;

        var rows = await db.WorkflowRuns.AsNoTracking()
            .Select(row => new { row.WorkflowRunId, row.State })
            .ToListAsync(cancellationToken);
        var active = new HashSet<(string WorkflowRunId, string WorkId)>();
        var diagnostics = new List<string>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var run = JSON.Deserialize<WorkflowRun>(row.State)
                    ?? throw new InvalidOperationException("deserialized to null");
                foreach (var task in run.Stages.SelectMany(stage => stage.Tasks))
                {
                    if (task.Status != TaskRunStatus.Running)
                        continue;
                    var workId = task.WorkId ?? task.Id;
                    if (!string.IsNullOrWhiteSpace(workId))
                        active.Add((row.WorkflowRunId, workId));
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add($"WorkflowRun '{row.WorkflowRunId}': {exception.Message}");
            }
        }

        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "DispatchSnapshot orphan sweep preflight failed:\n"
                + string.Join("\n", diagnostics));
        }

        var orphans = snapshots
            .Where(snapshot => !active.Contains((snapshot.WorkflowRunId, snapshot.WorkId)))
            .Select(snapshot => (snapshot.WorkflowRunId, snapshot.WorkId))
            .ToList();
        if (orphans.Count == 0)
            return 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var group in orphans.GroupBy(orphan => orphan.WorkflowRunId, StringComparer.Ordinal))
            {
                foreach (var chunk in group.Select(orphan => orphan.WorkId).Chunk(500))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await db.WorkflowDispatchSnapshots
                        .Where(snapshot => snapshot.WorkflowRunId == group.Key && chunk.Contains(snapshot.WorkId))
                        .ExecuteDeleteAsync(cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        logger?.LogInformation("DispatchSnapshot orphan sweep deleted {Count} rows", orphans.Count);
        return orphans.Count;
    }

    internal static string StripDispatchSnapshots(
        string json,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        out bool changed)
    {
        using var document = JsonDocument.Parse(json);
        var state = new RewriteState();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRunObject(document.RootElement, writer, workflowRunId, externalized, state);
        }
        changed = state.Changed;
        return changed ? Encoding.UTF8.GetString(buffer.ToArray()) : json;
    }

    private sealed class RewriteState
    {
        public bool Changed;
    }

    private static void WriteRunObject(
        JsonElement root,
        Utf8JsonWriter writer,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        RewriteState state)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            root.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in root.EnumerateObject())
        {
            if (IsProperty(property, "stages") && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(property.Name);
                WriteStagesArray(property.Value, writer, workflowRunId, externalized, state);
            }
            else
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteStagesArray(
        JsonElement stages,
        Utf8JsonWriter writer,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        RewriteState state)
    {
        writer.WriteStartArray();
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object)
            {
                stage.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            foreach (var property in stage.EnumerateObject())
            {
                if (IsProperty(property, "tasks") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    WriteTasksArray(property.Value, writer, workflowRunId, externalized, state);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTasksArray(
        JsonElement tasks,
        Utf8JsonWriter writer,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        RewriteState state)
    {
        writer.WriteStartArray();
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind == JsonValueKind.Object)
                WriteTaskObject(task, writer, workflowRunId, externalized, state);
            else
                task.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    private static void WriteTaskObject(
        JsonElement task,
        Utf8JsonWriter writer,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        RewriteState state)
    {
        string? status = null;
        string? workId = null;
        string? id = null;
        JsonElement? snapshot = null;
        foreach (var property in task.EnumerateObject())
        {
            if (IsProperty(property, "status") && property.Value.ValueKind == JsonValueKind.String)
                status = property.Value.GetString();
            else if (IsProperty(property, "workId") && property.Value.ValueKind == JsonValueKind.String)
                workId = property.Value.GetString();
            else if (IsProperty(property, "id") && property.Value.ValueKind == JsonValueKind.String)
                id = property.Value.GetString();
            else if (IsProperty(property, DispatchSnapshot))
                snapshot = property.Value;
        }

        if (snapshot.HasValue)
        {
            state.Changed = true;
            if (string.Equals(status, nameof(TaskRunStatus.Running), StringComparison.OrdinalIgnoreCase)
                && snapshot.Value.ValueKind != JsonValueKind.Null)
            {
                var key = !string.IsNullOrWhiteSpace(workId) ? workId : id;
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("Running task with dispatchSnapshot has no WorkId or Id");
                externalized.Add((workflowRunId, key, snapshot.Value.GetRawText()));
            }
        }

        writer.WriteStartObject();
        foreach (var property in task.EnumerateObject())
        {
            if (!IsProperty(property, DispatchSnapshot))
                property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static bool IsProperty(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);
}
