using Microsoft.Extensions.Options;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Otel;

/// <summary>
/// Storage-budget maintenance callback. Each invocation runs while
/// the observation loop is enabled and, on every 10s tick:
///
/// <list type="number">
///   <item>reads the latest probe usage,</item>
///   <item>while usage is at or above the high watermark (90% of
///     <see cref="OtelOptions.StorageBudgetBytes"/>), deletes oldest
///     complete Traces by <c>start_time</c> in a bounded batch,</item>
///   <item>after each eviction batch re-probes via
///     <see cref="IOtelStorageProbe"/>,</item>
///   <item>issues a bounded <c>incremental_vacuum(pages)</c> and a
///     truncating <c>wal_checkpoint(TRUNCATE)</c> to reclaim space,</item>
///   <item>arbitrates admission through <see cref="OtelStorageGuard"/>
///     so the write path can refuse new Traces if eviction cannot
///     keep up.</item>
/// </list>
///
/// All work executes under the sampler's existing
/// <c>SuppressInstrumentationScope</c> and <c>_enabled</c> gate.
/// </summary>
public interface IOtelStorageReclaimer
{
    bool Reclaim(OtelDb db, CancellationToken cancellationToken, Action<string>? statementObserver = null);
}

public sealed class SqliteOtelStorageReclaimer : IOtelStorageReclaimer
{
    public bool Reclaim(OtelDb db, CancellationToken cancellationToken, Action<string>? statementObserver = null)
    {
        using var connection = db.OpenReadWriteConnection();
        cancellationToken.ThrowIfCancellationRequested();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA incremental_vacuum({OtelDb.IncrementalVacuumPages});";
            statementObserver?.Invoke(command.CommandText);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when (IsBusyOrLocked(exception))
            {
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            statementObserver?.Invoke(command.CommandText);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when (IsBusyOrLocked(exception))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBusyOrLocked(Microsoft.Data.Sqlite.SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;
}

public sealed class OtelStorageMaintenance : IOtelMaintenanceCallback
{
    /// <summary>
    /// Maximum number of complete Traces a single size-eviction
    /// iteration deletes. Internal constant; not user-tunable. Bounds
    /// the per-tick work and keeps the cost proportional to the
    /// batch, not to the total history.
    /// </summary>
    public const int BatchSize = 100;

    private readonly OtelDb _db;
    private readonly IOtelStorageProbe _probe;
    private readonly OtelStorageGuard _guard;
    private readonly TimeProvider _timeProvider;
    private readonly IOtelStorageReclaimer _reclaimer;
    private readonly Action<string>? _statementObserver;

    public OtelStorageMaintenance(
        OtelDb db,
        IOtelStorageProbe probe,
        OtelStorageGuard guard,
        TimeProvider timeProvider,
        IOtelStorageReclaimer reclaimer,
        IOptions<OtelOptions> options)
        : this(db, probe, guard, timeProvider, reclaimer, options, null)
    {
    }

    public OtelStorageMaintenance(
        OtelDb db,
        IOtelStorageProbe probe,
        OtelStorageGuard guard,
        TimeProvider timeProvider,
        IOtelStorageReclaimer reclaimer,
        IOptions<OtelOptions> options,
        Action<string>? statementObserver)
        : this(db, probe, guard, timeProvider, reclaimer, (options ?? throw new ArgumentNullException(nameof(options))).Value, statementObserver)
    {
    }

    public OtelStorageMaintenance(
        OtelDb db,
        IOtelStorageProbe probe,
        OtelStorageGuard guard,
        TimeProvider timeProvider,
        IOtelStorageReclaimer reclaimer,
        OtelOptions options,
        Action<string>? statementObserver)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(reclaimer);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _probe = probe;
        _guard = guard;
        _timeProvider = timeProvider;
        _reclaimer = reclaimer;
        _statementObserver = statementObserver;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            var initial = _probe.Probe();
            if (initial.UsageBytes < _guard.HighWatermarkBytes)
            {
                // Below the high watermark: no eviction needed,
                // but still publish the arbitration so the guard's
                // state matches the probe (this also clears a stale
                // closed state if usage has since dropped).
                _guard.Arbitrate(initial.UsageBytes);
                return;
            }

            var usage = initial.UsageBytes;
            while (usage >= _guard.LowWatermarkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var traceIds = SelectOldestTraceIds(cancellationToken);
                if (traceIds.Count == 0)
                    break;

                DeleteBatch(traceIds, cancellationToken);
                var batchReclamationBlocked = _reclaimer.Reclaim(_db, cancellationToken, _statementObserver);
                if (batchReclamationBlocked)
                {
                    _guard.Arbitrate(usage, reclamationBlocked: true);
                    return;
                }

                var next = _probe.Probe();
                usage = next.UsageBytes;
                if (usage < _guard.LowWatermarkBytes)
                    break;
            }

            var reclamationBlocked = _reclaimer.Reclaim(_db, cancellationToken, _statementObserver);

            var final = _probe.Probe();
            _guard.Arbitrate(final.UsageBytes, reclamationBlocked);
        }, cancellationToken).ConfigureAwait(false);
    }

    private List<string> SelectOldestTraceIds(CancellationToken cancellationToken)
    {
        var ids = new List<string>(BatchSize);
        using var connection = _db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {OtelDb.TracesTraceIdColumn}
            FROM {OtelDb.TracesTable}
            WHERE {OtelDb.TracesEndTimeColumn} < $nowIso
            ORDER BY {OtelDb.TracesStartTimeColumn} ASC
            LIMIT $batch;
            """;
        // Removable = end_time strictly before now (Traces still
        // being collected have end_time >= now and must not be
        // evicted). end_time is stored as ISO 8601 UTC text so
        // lexicographic comparison matches chronological. The
        // query plan is index-served by idx_traces_end and the
        // result set is bounded by BatchSize.
        var nowIso = FormatTimestamp(_timeProvider.GetUtcNow());
        cmd.Parameters.AddWithValue("$nowIso", nowIso);
        cmd.Parameters.AddWithValue("$batch", BatchSize);
        _statementObserver?.Invoke(cmd.CommandText);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        cancellationToken.ThrowIfCancellationRequested();
        return ids;
    }

    private void DeleteBatch(List<string> traceIds, CancellationToken cancellationToken)
    {
        if (traceIds.Count == 0)
            return;

        using var connection = _db.OpenReadWriteConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            DeleteSpans(connection, transaction, traceIds);
            cancellationToken.ThrowIfCancellationRequested();
            DeleteTraces(connection, transaction, traceIds);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void DeleteSpans(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        List<string> traceIds)
    {
        var placeholders = string.Join(", ", traceIds.Select((_, i) => $"$id{i}"));
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"DELETE FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} IN ({placeholders});";
        for (var i = 0; i < traceIds.Count; i++)
            cmd.Parameters.AddWithValue($"$id{i}", traceIds[i]);
        RunCommand(cmd);
    }

    private void DeleteTraces(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        List<string> traceIds)
    {
        var placeholders = string.Join(", ", traceIds.Select((_, i) => $"$id{i}"));
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"DELETE FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} IN ({placeholders});";
        for (var i = 0; i < traceIds.Count; i++)
            cmd.Parameters.AddWithValue($"$id{i}", traceIds[i]);
        RunCommand(cmd);
    }

    private void RunCommand(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        _statementObserver?.Invoke(cmd.CommandText);
        cmd.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            System.Globalization.CultureInfo.InvariantCulture);
}
