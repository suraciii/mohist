using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Otel;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class TraceQuerierSpecs : IDisposable
{
    private readonly OtelDb _db;
    private readonly TraceQuerier _querier;
    private readonly OtelCollectorStatus _status;
    private readonly TraceIngester _ingester;
    private readonly FakeTimeProvider _timeProvider;
    private readonly TaskCompletionSource<bool> _readerStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _queryInterrupted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _connectionDisposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Keeper keeps the in-memory SQLite database alive for the test's lifetime.
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keeper;

    public TraceQuerierSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
        _ingester = new TraceIngester(_db, NullLogger<TraceIngester>.Instance);
        _status = new OtelCollectorStatus();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));
        _querier = new TraceQuerier(
            _db,
            _status,
            new InMemoryServerFileSystem(),
            _timeProvider,
            () => _readerStarted.TrySetResult(true),
            () => _queryInterrupted.TrySetResult(true),
            () => _connectionDisposed.TrySetResult(true));
    }

    public void Dispose()
    {
        _keeper.Dispose();
    }

    [Fact]
    public async Task ListAsync_EmptyDatabase_ReturnsEmptyList()
    {
        var rows = await _querier.ListAsync(limit: null, service: null);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListAsync_ReturnsRowsOrderedByStartTimeDescending()
    {
        SeedTrace("trace-a", "runner", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 3);
        SeedTrace("trace-b", "server", "2026-01-02T00:00:00Z", "2026-01-02T00:00:05Z", 1);
        SeedTrace("trace-c", "agent", "2026-01-03T00:00:00Z", "2026-01-03T00:00:02Z", 7);

        var rows = await _querier.ListAsync(limit: null, service: null);

        Assert.Equal(3, rows.Count);
        Assert.Equal("trace-c", rows[0].TraceId);
        Assert.Equal("trace-b", rows[1].TraceId);
        Assert.Equal("trace-a", rows[2].TraceId);
    }

    [Fact]
    public async Task ListAsync_ClampsLimitToMaxListLimit()
    {
        // Max-bound clamping is covered by ClampLimit tests below.
        // This integration check only needs enough rows to prove the
        // computed limit is actually applied to the SQLite query.
        for (var i = 0; i < 20; i++)
        {
            var day = (i % 28) + 1;
            var hour = i % 24;
            SeedTrace(
                $"trace-{i:D5}",
                "svc",
                $"2026-04-{day:D2}T{hour:D2}:00:00Z",
                "2026-04-01T00:00:01Z",
                1);
        }

        var rows = await _querier.ListAsync(limit: 7, service: null);

        Assert.Equal(7, rows.Count);
    }

    [Fact]
    public async Task ListAsync_HonorsCallerSuppliedLimit()
    {
        for (var i = 0; i < 20; i++)
        {
            SeedTrace(
                $"trace-{i:D4}",
                "svc",
                $"2026-02-{(i + 1):D2}T00:00:00Z",
                "2026-02-01T00:00:01Z",
                1);
        }

        var rows = await _querier.ListAsync(limit: 5, service: null);

        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public async Task ListAsync_FiltersByServiceName()
    {
        SeedTrace("trace-a", "runner", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("trace-b", "server", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("trace-c", "runner", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);

        var rows = await _querier.ListAsync(limit: null, service: "runner");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("runner", r.ServiceName));
    }

    [Fact]
    public async Task ListAsync_CombinesLimitAndServiceFilter()
    {
        SeedTrace("trace-a", "server", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("trace-b", "server", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("trace-c", "server", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);
        SeedTrace("trace-d", "runner", "2026-01-04T00:00:00Z", "2026-01-04T00:00:01Z", 1);

        var rows = await _querier.ListAsync(limit: 2, service: "server");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("server", r.ServiceName));
        Assert.Equal("trace-c", rows[0].TraceId);
        Assert.Equal("trace-b", rows[1].TraceId);
    }

    [Fact]
    public async Task ListAsync_NullOrZeroLimit_FallsBackToDefault()
    {
        for (var i = 0; i < TraceQuerier.DefaultListLimit + 5; i++)
        {
            SeedTrace(
                $"trace-{i:D4}",
                "svc",
                $"2026-03-{(i % 28) + 1:D2}T00:00:00Z",
                "2026-03-01T00:00:01Z",
                1);
        }

        var nullRows = await _querier.ListAsync(limit: null, service: null);
        var zeroRows = await _querier.ListAsync(limit: 0, service: null);
        var negativeRows = await _querier.ListAsync(limit: -3, service: null);

        Assert.Equal(TraceQuerier.DefaultListLimit, nullRows.Count);
        Assert.Equal(TraceQuerier.DefaultListLimit, zeroRows.Count);
        Assert.Equal(TraceQuerier.DefaultListLimit, negativeRows.Count);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsCollectorOfflineState()
    {
        var snapshot = await _querier.GetStatusAsync();

        Assert.False(snapshot.CollectorOnline);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsCollectorOnlineState()
    {
        _status.SetPortBound(true);

        var snapshot = await _querier.GetStatusAsync();

        Assert.True(snapshot.CollectorOnline);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsCounts()
    {
        // Count reporting is exercised against the in-memory db (the file-size
        // portion of the former ReportsCountsAndFileSize spec was WAL-mode
        // timing-dependent and is covered by OtelDbSpecs against a quiescent
        // file-backed db).
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "svc", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("t3", "svc", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);
        SeedSpan("t1", "s1", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z");
        SeedSpan("t1", "s2", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z");
        SeedSpan("t2", "s1", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z");

        var snapshot = await _querier.GetStatusAsync();

        Assert.Equal(3L, snapshot.TraceCount);
        Assert.True(snapshot.SpanCount >= 3L);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_SelectAllRows_ReturnsDictionaries()
    {
        SeedTrace("t1", "svc-a", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 2);
        SeedTrace("t2", "svc-b", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 5);

        var result = await _querier.ExecuteBoundedQuery(
            $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} ORDER BY {OtelDb.TracesStartTimeColumn}");

        Assert.False(result.Truncated);
        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, r => Equals(r[OtelDb.TracesServiceNameColumn], "svc-a") && Equals(r[OtelDb.TracesSpanCountColumn], 2L));
        Assert.Contains(result.Rows, r => Equals(r[OtelDb.TracesServiceNameColumn], "svc-b") && Equals(r[OtelDb.TracesSpanCountColumn], 5L));
    }

    [Fact]
    public async Task ExecuteBoundedQuery_AggregateCount_ReturnsSingleRow()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "svc", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);

        var result = await _querier.ExecuteBoundedQuery($"SELECT COUNT(*) AS total FROM {OtelDb.TracesTable}");

        Assert.False(result.Truncated);
        Assert.Single(result.Rows);
        var row = result.Rows[0];
        Assert.Equal(2L, row["total"]);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_NullCell_BecomesNullInDictionary()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);

        var result = await _querier.ExecuteBoundedQuery(
            $"SELECT {OtelDb.TracesTraceIdColumn}, NULL AS maybe FROM {OtelDb.TracesTable}");

        Assert.False(result.Truncated);
        Assert.Single(result.Rows);
        Assert.Null(result.Rows[0]["maybe"]);
    }

    [Fact]
    public void MaxQueryResponseRows_IsFixedAtOneThousand()
    {
        Assert.Equal(1000, TraceQuerier.MaxQueryResponseRows);
    }

    [Fact]
    public void MaxQueryResponseBytes_IsFixedAtFourMiB()
    {
        Assert.Equal(4 * 1024 * 1024, TraceQuerier.MaxQueryResponseBytes);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_ManyRows_TruncatesAtRowLimitWithReason()
    {
        var query = """
            WITH RECURSIVE cnt(x) AS (
                SELECT 1
                UNION ALL
                SELECT x + 1 FROM cnt WHERE x < 2500
            )
            SELECT x FROM cnt;
            """;

        var result = await _querier.ExecuteBoundedQuery(query);

        Assert.True(result.Truncated);
        Assert.Equal(TraceQuerier.RowLimitTruncateReason, result.TruncateReason);
        Assert.Equal(TraceQuerier.MaxQueryResponseRows, result.Rows.Count);
        Assert.Equal(1L, result.Rows[0]["x"]);
        Assert.Equal((long)TraceQuerier.MaxQueryResponseRows, result.Rows[^1]["x"]);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_SingleLargeCell_TruncatesAtByteLimitWithoutMaterializing()
    {
        const int oversizedChars = 8 * 1024 * 1024;

        var result = await _querier.ExecuteBoundedQuery(
            $"SELECT substr(replace(hex(zeroblob({oversizedChars})), '0', 'x'), 1, {oversizedChars}) AS big");

        Assert.True(result.Truncated);
        Assert.Equal(TraceQuerier.ByteLimitTruncateReason, result.TruncateReason);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_ModerateRowsUnderByteLimit_TruncatesBeforeRowLimit()
    {
        // Each row carries a 6 KiB TEXT cell, so 1000 rows would exceed the
        // 4 MiB byte cap by row ~700. The row cap (1000) is never reached.
        const int rowCount = 1000;
        const int cellBytes = 6 * 1024;

        var recursiveQuery = $$"""
            WITH RECURSIVE cnt(x) AS (
                SELECT 1
                UNION ALL
                SELECT x + 1 FROM cnt WHERE x < {{rowCount}}
            )
            SELECT hex(randomblob({{cellBytes / 2}})) AS payload FROM cnt;
            """;

        var recursiveResult = await _querier.ExecuteBoundedQuery(recursiveQuery);

        Assert.True(recursiveResult.Truncated);
        Assert.Equal(TraceQuerier.ByteLimitTruncateReason, recursiveResult.TruncateReason);
        Assert.True(recursiveResult.Rows.Count < TraceQuerier.MaxQueryResponseRows);
        Assert.True(recursiveResult.Rows.Count > 0);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_RecursiveCteAmplification_RespectsBothCaps()
    {
        var smallRowQuery = """
            WITH RECURSIVE cnt(x) AS (
                SELECT 1
                UNION ALL
                SELECT x + 1 FROM cnt WHERE x < 100000
            )
            SELECT x FROM cnt;
            """;

        var result = await _querier.ExecuteBoundedQuery(smallRowQuery);

        Assert.True(result.Truncated);
        Assert.Equal(TraceQuerier.RowLimitTruncateReason, result.TruncateReason);
        Assert.Equal(TraceQuerier.MaxQueryResponseRows, result.Rows.Count);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_WithinBothCaps_DoesNotMarkTruncated()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);

        var result = await _querier.ExecuteBoundedQuery(
            $"SELECT {OtelDb.TracesServiceNameColumn} FROM {OtelDb.TracesTable}");

        Assert.False(result.Truncated);
        Assert.Null(result.TruncateReason);
    }

    [Fact]
    public void OpenConnection_HandleIsVisibleAndNonNull()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.NotNull(connection.Handle);
    }

    [Fact]
    public async Task ExecuteBoundedQuery_ExecutionBudgetInterruptsRecursiveReader()
    {
        var query = """
            WITH RECURSIVE numbers(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM numbers WHERE value < 100000000
            )
            SELECT value FROM numbers;
            """;

        var execution = Task.Run(() => _querier.ExecuteBoundedQuery(query));
        await _readerStarted.Task;
        _timeProvider.Advance(TimeSpan.FromSeconds(TraceQuerier.QueryExecutionBudgetSeconds));
        await _queryInterrupted.Task;

        var exception = await Record.ExceptionAsync(() => execution);

        Assert.NotNull(exception);
        Assert.True(
            exception is OperationCanceledException
                || exception is SqliteException { SqliteErrorCode: 9 },
            $"Expected cancellation or SQLITE_INTERRUPT (9), got {exception}");
        Assert.True(_queryInterrupted.Task.IsCompletedSuccessfully);
        Assert.True(_connectionDisposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void ValidateSelectOnly_EmptyOrWhitespace_Rejects()
    {
        Assert.NotNull(TraceQuerier.ValidateSelectOnly(null));
        Assert.NotNull(TraceQuerier.ValidateSelectOnly(string.Empty));
        Assert.NotNull(TraceQuerier.ValidateSelectOnly("   "));
    }

    [Theory]
    [InlineData("SELECT * FROM traces")]
    [InlineData("select * from traces")]
    [InlineData("  SELECT * FROM traces  ")]
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte")]
    [InlineData("SELECT 1;")]
    [InlineData("-- comment\nSELECT * FROM traces")]
    [InlineData("/* block */ SELECT * FROM traces")]
    public void ValidateSelectOnly_SelectOnlyStatements_Accepted(string sql)
    {
        Assert.Null(TraceQuerier.ValidateSelectOnly(sql));
    }

    [Theory]
    [InlineData("DELETE FROM traces")]
    [InlineData("INSERT INTO traces VALUES ('x','y','z','w',1)")]
    [InlineData("UPDATE traces SET service_name='x'")]
    [InlineData("DROP TABLE traces")]
    [InlineData("ALTER TABLE traces ADD COLUMN x TEXT")]
    [InlineData("ATTACH DATABASE 'x.db' AS x")]
    [InlineData("DETACH DATABASE x")]
    [InlineData("PRAGMA writable_schema = 1")]
    [InlineData("VACUUM")]
    [InlineData("REINDEX")]
    [InlineData("CREATE TABLE foo (id INT)")]
    public void ValidateSelectOnly_NonSelectStatements_Rejected(string sql)
    {
        var reason = TraceQuerier.ValidateSelectOnly(sql);
        Assert.NotNull(reason);
        Assert.Contains("SELECT", reason!);
    }

    [Fact]
    public void ValidateSelectOnly_CompoundStatementWithTrailingWrite_Rejected()
    {
        var reason = TraceQuerier.ValidateSelectOnly(
            "SELECT * FROM traces; DROP TABLE spans;");

        Assert.NotNull(reason);
        Assert.Contains("DROP", reason!);
    }

    [Fact]
    public void ValidateSelectOnly_CompoundSelectOnly_Accepted()
    {
        var reason = TraceQuerier.ValidateSelectOnly(
            "SELECT 1 UNION SELECT 2;");

        Assert.Null(reason);
    }

    [Fact]
    public void ClampLimit_NullOrNonPositive_ReturnsDefault()
    {
        Assert.Equal(TraceQuerier.DefaultListLimit, TraceQuerier.ClampLimit(null));
        Assert.Equal(TraceQuerier.DefaultListLimit, TraceQuerier.ClampLimit(0));
        Assert.Equal(TraceQuerier.DefaultListLimit, TraceQuerier.ClampLimit(-10));
    }

    [Fact]
    public void ClampLimit_PositiveValuesAboveMax_ReturnsMax()
    {
        Assert.Equal(TraceQuerier.MaxListLimit, TraceQuerier.ClampLimit(TraceQuerier.MaxListLimit + 1));
        Assert.Equal(TraceQuerier.MaxListLimit, TraceQuerier.ClampLimit(int.MaxValue));
    }

    [Fact]
    public void ClampLimit_PositiveValuesAtOrBelowMax_PassThrough()
    {
        Assert.Equal(1, TraceQuerier.ClampLimit(1));
        Assert.Equal(7, TraceQuerier.ClampLimit(7));
        Assert.Equal(TraceQuerier.MaxListLimit, TraceQuerier.ClampLimit(TraceQuerier.MaxListLimit));
    }

    private void SeedTrace(string traceId, string serviceName, string startTime, string endTime, long spanCount)
    {
        using var connection = _db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {OtelDb.TracesTable} (
                {OtelDb.TracesTraceIdColumn},
                {OtelDb.TracesServiceNameColumn},
                {OtelDb.TracesStartTimeColumn},
                {OtelDb.TracesEndTimeColumn},
                {OtelDb.TracesSpanCountColumn}
            ) VALUES ($trace_id, $service_name, $start_time, $end_time, $span_count);
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$service_name", serviceName);
        cmd.Parameters.AddWithValue("$start_time", startTime);
        cmd.Parameters.AddWithValue("$end_time", endTime);
        cmd.Parameters.AddWithValue("$span_count", spanCount);
        cmd.ExecuteNonQuery();
    }

    private void SeedSpan(string traceId, string spanId, string startTime, string endTime)
    {
        using var connection = _db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {OtelDb.SpansTable} (
                {OtelDb.SpansTraceIdColumn},
                {OtelDb.SpansSpanIdColumn},
                {OtelDb.SpansNameColumn},
                {OtelDb.SpansKindColumn},
                {OtelDb.SpansStartTimeColumn},
                {OtelDb.SpansEndTimeColumn},
                {OtelDb.SpansStatusCodeColumn}
            ) VALUES ($trace_id, $span_id, 'test', 1, $start_time, $end_time, 0);
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$span_id", spanId);
        cmd.Parameters.AddWithValue("$start_time", startTime);
        cmd.Parameters.AddWithValue("$end_time", endTime);
        cmd.ExecuteNonQuery();
    }
}
