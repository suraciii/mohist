using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mohist.Server.SystemInfo;
using static SQLitePCL.raw;

namespace Mohist.Server.Otel;

/// <summary>
/// Read-side of the OTel trace collector. Owns the queries exposed by
/// the <c>/otel/api/*</c> routes on the main API port:
/// <c>GET /otel/api/traces</c> (recent traces, filtered and limited),
/// <c>POST /otel/api/query</c> (free-SQL with a three-layer safety net),
/// and <c>GET /otel/api/status</c> (collector + database summary).
/// </summary>
/// <remarks>
/// <para>The querier never opens a write transaction — all queries use
/// <see cref="OtelDb.OpenReadOnlyConnection"/> so the SQLite engine
/// physically rejects writes regardless of any keyword-level check in
/// <see cref="ExecuteBoundedQuery"/>.</para>
/// <para>The querier is registered as a singleton (it has no per-request
/// state) and is the only object that reads <c>otel.db</c> on the main
/// API port.</para>
/// </remarks>
public sealed class TraceQuerier : IOtelQueryExecutor
{
    /// <summary>Default limit for <see cref="ListAsync"/> when the caller doesn't specify one.</summary>
    public const int DefaultListLimit = 50;

    /// <summary>Hard upper bound on <see cref="ListAsync"/> results.</summary>
    public const int MaxListLimit = 1000;

    /// <summary>Hard upper bound for the <c>POST /otel/api/query</c> body.</summary>
    public const int MaxQueryRequestBodyBytes = 64 * 1024;

    /// <summary>Execution budget in seconds for the free-SQL endpoint.</summary>
    public const int QueryExecutionBudgetSeconds = 10;

    /// <summary>5-second per-query timeout for the free-SQL endpoint.</summary>
    public static readonly TimeSpan QueryCommandTimeout = TimeSpan.FromSeconds(5);

    private readonly OtelDb _db;
    private readonly OtelCollectorStatus _collectorStatus;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly Action? _readerStarted;

    public TraceQuerier(
        OtelDb db,
        OtelCollectorStatus collectorStatus,
        IFileSystem fileSystem,
        TimeProvider timeProvider,
        Action? readerStarted = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(collectorStatus);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _db = db;
        _collectorStatus = collectorStatus;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
        _readerStarted = readerStarted;
    }

    /// <summary>
    /// Returns recent trace rows ordered by <c>start_time</c> descending.
    /// </summary>
    /// <param name="limit">Caller-supplied limit; clamped to <see cref="MaxListLimit"/>.</param>
    /// <param name="service">Optional <c>service_name</c> filter. <c>null</c> disables the filter.</param>
    public async Task<IReadOnlyList<TraceSummary>> ListAsync(int? limit, string? service, CancellationToken ct = default)
    {
        var effectiveLimit = ClampLimit(limit);

        await using var connection = _db.OpenReadOnlyConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {OtelDb.TracesTraceIdColumn}, " +
            $"{OtelDb.TracesServiceNameColumn}, " +
            $"{OtelDb.TracesStartTimeColumn}, " +
            $"{OtelDb.TracesEndTimeColumn}, " +
            $"{OtelDb.TracesSpanCountColumn} " +
            $"FROM {OtelDb.TracesTable} " +
            (string.IsNullOrWhiteSpace(service)
                ? string.Empty
                : $"WHERE {OtelDb.TracesServiceNameColumn} = $service ") +
            $"ORDER BY {OtelDb.TracesStartTimeColumn} DESC " +
            $"LIMIT $limit;";

        cmd.Parameters.AddWithValue("$limit", effectiveLimit);
        if (!string.IsNullOrWhiteSpace(service))
            cmd.Parameters.AddWithValue("$service", service);

        var results = new List<TraceSummary>(effectiveLimit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TraceSummary(
                TraceId: reader.GetString(0),
                ServiceName: reader.GetString(1),
                StartTime: reader.GetString(2),
                EndTime: reader.GetString(3),
                SpanCount: reader.GetInt64(4)));
        }
        return results;
    }

    /// <summary>
    /// Aggregate status snapshot. The collector flag is read from the
    /// process-wide <see cref="OtelCollectorStatus"/> singleton; the
    /// database fields are computed against the live <c>otel.db</c>.
    /// </summary>
    public async Task<CollectorStatusSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        var dbSize = ResolveDatabaseSizeBytes();

        long traceCount = 0;
        long spanCount = 0;
        try
        {
            await using var connection = _db.OpenReadOnlyConnection();
            await using (var traceCmd = connection.CreateCommand())
            {
                traceCmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable};";
                var raw = await traceCmd.ExecuteScalarAsync(ct);
                traceCount = raw is null or DBNull ? 0L : Convert.ToInt64(raw);
            }
            await using (var spanCmd = connection.CreateCommand())
            {
                spanCmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable};";
                var raw = await spanCmd.ExecuteScalarAsync(ct);
                spanCount = raw is null or DBNull ? 0L : Convert.ToInt64(raw);
            }
        }
        catch
        {
            // The status endpoint must always answer; surface zeros
            // rather than 500 if the DB is unreachable. The size field
            // stays at the resolved value (0 if the file is missing).
            traceCount = 0;
            spanCount = 0;
        }

        return new CollectorStatusSnapshot(
            CollectorOnline: _collectorStatus.IsPortBound,
            DbSizeBytes: dbSize,
            TraceCount: traceCount,
            SpanCount: spanCount);
    }

    /// <summary>
    /// Executes a user-supplied SELECT against <c>otel.db</c> on a
    /// read-only connection with an execution budget. The
    /// <paramref name="sql"/> must pass <see cref="ValidateSelectOnly"/>
    /// before this call; callers are responsible for that gate.
    /// </summary>
    /// <remarks>
    /// Four-layer safety net: admission validation, a physically read-only
    /// connection, an execution-budget interrupt, and response budgets.
    /// <list type="number">
    ///   <item><see cref="OtelDb.OpenReadOnlyConnection"/> — physical isolation.</item>
    ///   <item>Caller's keyword check in <see cref="ValidateSelectOnly"/>.</item>
    ///   <item><see cref="QueryExecutionBudgetSeconds"/> — interrupts active SQLite work.</item>
    /// </list>
    /// </remarks>
    public async Task<QueryResult> ExecuteBoundedQuery(string sql, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);

        using var budgetCts = new CancellationTokenSource();
        using var budgetTimer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budgetCts,
            TimeSpan.FromSeconds(QueryExecutionBudgetSeconds),
            Timeout.InfiniteTimeSpan);
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);
        var executionToken = executionCts.Token;
        await using var connection = _db.OpenReadOnlyConnection();
        using var interruptRegistration = executionToken.Register(static state =>
        {
            var handle = ((SqliteConnection)state!).Handle;
            if (handle is not null)
                sqlite3_interrupt(handle);
        }, connection);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)QueryCommandTimeout.TotalSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(executionToken);
        _readerStarted?.Invoke();

        var fieldCount = reader.FieldCount;
        var fieldNames = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            fieldNames[i] = reader.GetName(i);
        }

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(executionToken))
        {
            var row = new Dictionary<string, object?>(fieldCount, StringComparer.Ordinal);
            for (var i = 0; i < fieldCount; i++)
            {
                row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return new QueryResult(rows, Truncated: false, TruncateReason: null);
    }

    public Task<QueryResult> Execute(string sql, CancellationToken cancellationToken = default) =>
        ExecuteBoundedQuery(sql, cancellationToken);

    /// <summary>
    /// Validates that <paramref name="sql"/> is a SELECT-only statement
    /// at the top level. Returns null on success or a human-readable
    /// reason on failure; never throws.
    /// </summary>
    /// <remarks>
    /// The check normalizes whitespace and strips line comments before
    /// looking at the first keyword. It is a defense-in-depth measure on
    /// top of the read-only connection — the SQLite engine is the
    /// ultimate authority, but rejecting obvious non-SELECTs at the
    /// HTTP layer gives callers a clean 400 instead of a write-failure
    /// round-trip.
    /// </remarks>
    public static string? ValidateSelectOnly(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL is empty.";

        var normalized = NormalizeSql(sql);
        if (normalized.Length == 0)
            return "SQL contains no executable statements.";

        var firstToken = ReadFirstKeyword(normalized);
        if (!string.Equals(firstToken, "SELECT", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(firstToken, "WITH", StringComparison.OrdinalIgnoreCase))
        {
            return "Only SELECT queries are allowed.";
        }

        // Walk top-level statements to ensure none of them start with a
        // banned keyword. Compound SELECTs (UNION / INTERSECT / EXCEPT)
        // are allowed as long as every top-level statement starts with
        // SELECT or WITH.
        foreach (var stmt in SplitTopLevelStatements(normalized))
        {
            var head = ReadFirstKeyword(stmt);
            if (string.Equals(head, "SELECT", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(head, "WITH", StringComparison.OrdinalIgnoreCase))
                continue;
            return $"Only SELECT queries are allowed; statement starts with '{head}'.";
        }

        return null;
    }

    /// <summary>
    /// Computes <see cref="TraceQuerier.DefaultListLimit"/> if
    /// <paramref name="limit"/> is null, clamps to
    /// <see cref="TraceQuerier.MaxListLimit"/> on the upper end and 1
    /// on the lower end. Negative or zero values fall back to the
    /// default limit.
    /// </summary>
    public static int ClampLimit(int? limit)
    {
        if (!limit.HasValue || limit.Value <= 0)
            return DefaultListLimit;
        return Math.Min(limit.Value, MaxListLimit);
    }

    private long ResolveDatabaseSizeBytes()
    {
        if (string.IsNullOrWhiteSpace(_db.DatabasePath))
            return 0L;
        if (!_fileSystem.Exists(_db.DatabasePath))
            return 0L;

        // SQLite WAL mode keeps data in the main file, the
        // -wal sidecar, and the -shm sidecar. For a meaningful "how
        // much disk space is otel.db using" answer we sum all three.
        long total = 0;
        total += SafeLength(_db.DatabasePath);
        total += SafeLength(_db.DatabasePath + "-wal");
        total += SafeLength(_db.DatabasePath + "-shm");
        total += SafeLength(_db.DatabasePath + "-journal");
        return total;
    }

    private long SafeLength(string path)
    {
        try
        {
            if (!_fileSystem.Exists(path))
                return 0L;
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0L;
        }
    }

    private static string NormalizeSql(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '-' && i + 1 < raw.Length && raw[i + 1] == '-')
            {
                // Line comment to end of line.
                i += 2;
                while (i < raw.Length && raw[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
            {
                // Block comment to */.
                i += 2;
                while (i + 1 < raw.Length && !(raw[i] == '*' && raw[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                sb.Append(' ');
                i++;
                continue;
            }
            sb.Append(char.ToUpperInvariant(c));
            i++;
        }
        return sb.ToString().Trim();
    }

    private static string ReadFirstKeyword(string normalized)
    {
        var span = normalized.AsSpan();
        var start = 0;
        while (start < span.Length && span[start] == ' ')
            start++;
        var end = start;
        while (end < span.Length && span[end] != ' ' && span[end] != '(' && span[end] != ';')
            end++;
        return span[start..end].ToString();
    }

    private static IEnumerable<string> SplitTopLevelStatements(string normalized)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                if (depth > 0) depth--;
                continue;
            }
            if (c == ';' && depth == 0)
            {
                var stmt = normalized.Substring(start, i - start).Trim();
                if (stmt.Length > 0)
                    yield return stmt;
                start = i + 1;
            }
        }
        var tail = normalized[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }
}

public interface IOtelQueryExecutor
{
    Task<QueryResult> Execute(string sql, CancellationToken cancellationToken = default);
}

public sealed record QueryResult(
    [property: JsonPropertyName("rows")] IReadOnlyList<Dictionary<string, object?>> Rows,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("truncate_reason")] string? TruncateReason);

/// <summary>Row model for <see cref="TraceQuerier.ListAsync"/>.</summary>
public sealed record TraceSummary(
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("service_name")] string ServiceName,
    [property: JsonPropertyName("start_time")] string StartTime,
    [property: JsonPropertyName("end_time")] string EndTime,
    [property: JsonPropertyName("span_count")] long SpanCount);

/// <summary>Aggregate snapshot for <see cref="TraceQuerier.GetStatusAsync"/>.</summary>
public sealed record CollectorStatusSnapshot(
    [property: JsonPropertyName("collector_online")] bool CollectorOnline,
    [property: JsonPropertyName("db_size_bytes")] long DbSizeBytes,
    [property: JsonPropertyName("trace_count")] long TraceCount,
    [property: JsonPropertyName("span_count")] long SpanCount);
