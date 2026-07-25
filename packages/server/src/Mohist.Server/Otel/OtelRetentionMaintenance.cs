using System.Globalization;
using Microsoft.Extensions.Options;

namespace Mohist.Server.Otel;

/// <summary>
/// Retention-by-age maintenance callback. Each invocation runs while the
/// observation loop is enabled and deletes, in one bounded transaction,
/// the complete Traces (header + every sharing Span row) whose
/// <c>end_time</c> is older than the configured retention age. The work
/// per invocation is bounded by an internal batch size; remaining aged
/// Traces are removed by subsequent invocations in oldest-first order.
/// Time comparisons use the injected <see cref="TimeProvider"/>; the
/// callback is a no-op for in-window Traces.
/// </summary>
public class OtelRetentionMaintenance : IOtelMaintenanceCallback
{
    /// <summary>
    /// Maximum number of complete Traces a single invocation deletes.
    /// Internal constant; not user-tunable. Bounds the per-tick work
    /// and keeps the maintenance cost proportional to the batch, not to
    /// the total history.
    /// </summary>
    public const int BatchSize = 100;

    private readonly OtelDb _db;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retentionMaxAge;
    private readonly Action<string>? _statementObserver;

    public OtelRetentionMaintenance(OtelDb db, TimeProvider timeProvider, IOptions<OtelOptions> options)
        : this(db, timeProvider, (options ?? throw new ArgumentNullException(nameof(options))).Value.RetentionMaxAge, null)
    {
    }

    public OtelRetentionMaintenance(OtelDb db, TimeProvider timeProvider, TimeSpan retentionMaxAge)
        : this(db, timeProvider, retentionMaxAge, null)
    {
    }

    public OtelRetentionMaintenance(
        OtelDb db,
        TimeProvider timeProvider,
        TimeSpan retentionMaxAge,
        Action<string>? statementObserver)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (retentionMaxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionMaxAge));

        _db = db;
        _timeProvider = timeProvider;
        _retentionMaxAge = retentionMaxAge;
        _statementObserver = statementObserver;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _retentionMaxAge;
        var cutoffIso = FormatTimestamp(cutoff);

        await Task.Run(() => EvictAgedBatch(cutoffIso, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private void EvictAgedBatch(string cutoffIso, CancellationToken cancellationToken)
    {
        using var connection = _db.OpenReadWriteConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var traceIds = SelectAgedTraceIds(connection, transaction, cutoffIso);
            if (traceIds.Count == 0)
            {
                transaction.Commit();
                return;
            }

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

    private List<string> SelectAgedTraceIds(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string cutoffIso)
    {
        var ids = new List<string>(BatchSize);
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT {OtelDb.TracesTraceIdColumn}
            FROM {OtelDb.TracesTable}
            WHERE {OtelDb.TracesEndTimeColumn} < $cutoff
            ORDER BY {OtelDb.TracesStartTimeColumn} ASC
            LIMIT $batch;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffIso);
        cmd.Parameters.AddWithValue("$batch", BatchSize);
        _statementObserver?.Invoke(cmd.CommandText);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
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
        RunDelete(cmd);
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
        RunDelete(cmd);
    }

    private void RunDelete(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        _statementObserver?.Invoke(cmd.CommandText);
        cmd.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);
}
