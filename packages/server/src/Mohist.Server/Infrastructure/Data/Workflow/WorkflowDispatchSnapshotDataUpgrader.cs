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

// Cold-start migration for the dispatch-snapshot externalization: strips the
// legacy per-attempt "dispatchSnapshot" member from every task; a Running
// attempt's snapshot is externalized into WorkflowDispatchSnapshots (INSERT OR
// IGNORE) so redelivery survives the format change. Mirrors the established
// State upgrader shape: no-write preflight, verified SQLite backup, single
// transaction, byte-level idempotency. A separate startup sweep drops snapshot
// rows whose task is no longer Running.
public static class WorkflowDispatchSnapshotDataUpgrader
{
    private const string DispatchSnapshot = "dispatchSnapshot";
    private const string Running = "Running";

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
        var externalized = new List<(string WorkflowRunId, string WorkId, string SnapshotJson)>();
        var diagnostics = new List<string>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stripped = StripDispatchSnapshots(row.State, row.WorkflowRunId, externalized, out var changed);
                if (!changed)
                    continue;
                // Preflight: the stripped State must still deserialize as a run.
                if (JSON.Deserialize<WorkflowRun>(stripped) is null)
                    throw new InvalidOperationException("deserialized to null");
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

            // INSERT OR IGNORE: skip snapshot keys that already exist (idempotent
            // re-run, or a row pre-populated before this migration).
            var runIds = externalized.Select(s => s.WorkflowRunId).Distinct().ToList();
            var existingKeys = runIds.Count > 0
                ? (await db.WorkflowDispatchSnapshots.AsNoTracking()
                    .Where(s => runIds.Contains(s.WorkflowRunId))
                    .Select(s => new { s.WorkflowRunId, s.WorkId })
                    .ToListAsync(cancellationToken))
                    .Select(k => (k.WorkflowRunId, k.WorkId))
                    .ToHashSet()
                : new HashSet<(string, string)>();
            foreach (var snap in externalized)
            {
                if (existingKeys.Contains((snap.WorkflowRunId, snap.WorkId)))
                    continue;
                db.WorkflowDispatchSnapshots.Add(new WorkflowDispatchSnapshotRow
                {
                    WorkflowRunId = snap.WorkflowRunId,
                    WorkId = snap.WorkId,
                    SnapshotJson = snap.SnapshotJson,
                });
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
            "DispatchSnapshot externalization committed: writtenCount={WrittenCount}, externalizedCount={ExternalizedCount}, backupPath={BackupPath}",
            upgrades.Count,
            externalized.Count,
            backupPath);
        return new WorkflowDispatchSnapshotUpgradeResult(upgrades.Count, upgrades.Count, externalized.Count, 0, backupPath);
    }

    // Deletes snapshot rows whose task is no longer Running (terminal, superseded,
    // or whose run no longer exists). Safe at cold start: grains are inactive, so
    // persisted State is the authoritative liveness source, and a mislabeled
    // orphan is unrecoverable for redelivery anyway (its task is not Running).
    public static async Task<int> SweepOrphansAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        var snapshots = await db.WorkflowDispatchSnapshots.AsNoTracking()
            .OrderBy(s => s.WorkflowRunId).ThenBy(s => s.WorkId)
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0)
            return 0;

        var rows = await db.WorkflowRuns.AsNoTracking()
            .Select(row => new { row.WorkflowRunId, row.State })
            .ToListAsync(cancellationToken);
        var active = new HashSet<(string WorkflowRunId, string WorkId)>();
        foreach (var row in rows)
        {
            foreach (var workId in ReadRunningWorkIds(row.State))
                active.Add((row.WorkflowRunId, workId));
        }

        var orphans = snapshots
            .Where(s => !active.Contains((s.WorkflowRunId, s.WorkId)))
            .Select(s => (s.WorkflowRunId, s.WorkId))
            .ToList();
        if (orphans.Count == 0)
            return 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var group in orphans.GroupBy(o => o.WorkflowRunId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workIds = group.Select(g => g.WorkId).ToList();
                await db.WorkflowDispatchSnapshots
                    .Where(s => s.WorkflowRunId == group.Key && workIds.Contains(s.WorkId))
                    .ExecuteDeleteAsync(cancellationToken);
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
        var ctx = new RewriteState();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRunObject(document.RootElement, writer, workflowRunId, externalized, ctx);
        }
        changed = ctx.Changed;
        return changed ? Encoding.UTF8.GetString(buffer.ToArray()) : json;
    }

    internal static IReadOnlyList<string> ReadRunningWorkIds(string json)
    {
        var result = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("stages", out var stages)
                || stages.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var stage in stages.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Object
                    || !stage.TryGetProperty("tasks", out var tasks)
                    || tasks.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var task in tasks.EnumerateArray())
                {
                    if (task.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!task.TryGetProperty("status", out var status)
                        || status.ValueKind != JsonValueKind.String
                        || !string.Equals(status.GetString(), Running, StringComparison.Ordinal))
                        continue;
                    var workId = task.TryGetProperty("workId", out var w) && w.ValueKind == JsonValueKind.String
                        ? w.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(workId))
                        result.Add(workId!);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed State: treat its snapshots as orphans (swept). Reads are
            // best-effort; a corrupt row blocks the externalization preflight, not
            // this sweep.
        }
        return result;
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
        RewriteState ctx)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            root.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "stages", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(property.Name);
                WriteStagesArray(property.Value, writer, workflowRunId, externalized, ctx);
                continue;
            }

            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteStagesArray(
        JsonElement stages,
        Utf8JsonWriter writer,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        RewriteState ctx)
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
                if (string.Equals(property.Name, "tasks", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    WriteTasksArray(property.Value, writer, workflowRunId, externalized, ctx);
                    continue;
                }

                property.WriteTo(writer);
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
        RewriteState ctx)
    {
        writer.WriteStartArray();
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object)
            {
                task.WriteTo(writer);
                continue;
            }

            WriteTaskObject(task, writer, workflowRunId, externalized, ctx);
        }
        writer.WriteEndArray();
    }

    private static void WriteTaskObject(
        JsonElement task,
        Utf8JsonWriter writer,
        string workflowRunId,
        ICollection<(string WorkflowRunId, string WorkId, string SnapshotJson)> externalized,
        RewriteState ctx)
    {
        string? status = null;
        string? workId = null;
        string? id = null;
        var hasSnapshot = false;
        JsonElement snapshotValue = default;
        foreach (var property in task.EnumerateObject())
        {
            if (string.Equals(property.Name, "status", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                status = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "workId", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                workId = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "id", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                id = property.Value.GetString();
            }
            else if (string.Equals(property.Name, DispatchSnapshot, StringComparison.Ordinal))
            {
                hasSnapshot = true;
                snapshotValue = property.Value;
            }
        }

        if (hasSnapshot)
        {
            ctx.Changed = true;
            if (string.Equals(status, Running, StringComparison.Ordinal)
                && snapshotValue.ValueKind != JsonValueKind.Null)
            {
                var key = !string.IsNullOrWhiteSpace(workId) ? workId : id;
                if (!string.IsNullOrWhiteSpace(key))
                    externalized.Add((workflowRunId, key!, snapshotValue.GetRawText()));
            }
        }

        writer.WriteStartObject();
        foreach (var property in task.EnumerateObject())
        {
            if (string.Equals(property.Name, DispatchSnapshot, StringComparison.Ordinal))
                continue;
            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
