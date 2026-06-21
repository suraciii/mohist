using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Otel;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Telemetry;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Telemetry)]
public class TraceQuerierSpecs : IDisposable
{
    private readonly string _dataDir;
    private readonly OtelDb _db;
    private readonly TraceQuerier _querier;
    private readonly OtelCollectorStatus _status;
    private readonly TraceIngester _ingester;

    public TraceQuerierSpecs()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"mohist-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var dbPath = Path.Combine(_dataDir, "otel.db");

        var env = new MockEnvironment();
        _db = new OtelDb(new OtelOptions { DbPath = dbPath }, env, new PassthroughFileSystem());
        _ingester = new TraceIngester(_db, NullLogger<TraceIngester>.Instance);
        _status = new OtelCollectorStatus();
        _querier = new TraceQuerier(_db, _status, new PassthroughFileSystem());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
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
        // The unit-level clamp behavior is exercised by ClampLimit
        // tests below. Here we confirm the integration end-to-end: a
        // high limit doesn't return more than MaxListLimit.
        for (var i = 0; i < TraceQuerier.MaxListLimit + 5; i++)
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

        var rows = await _querier.ListAsync(limit: 5000, service: null);

        Assert.Equal(TraceQuerier.MaxListLimit, rows.Count);
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
    public async Task GetStatusAsync_NoDatabaseFile_ReportsZerosAndActualCollectorState()
    {
        // Recreate the querier against a path that doesn't exist (and
        // that no one will lazily create via the read-only bootstrap).
        var missingDir = Path.Combine(Path.GetTempPath(), $"mohist-otel-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(missingDir);
        try
        {
            var env = new MockEnvironment();
            var options = new OtelOptions { DbPath = Path.Combine(missingDir, "otel.db"), Enabled = false };
            var missingDb = new OtelDb(options, env, new PassthroughFileSystem());
            var status = new OtelCollectorStatus();
            status.SetPortBound(true);
            var querier = new TraceQuerier(missingDb, status, new PassthroughFileSystem());

            var snapshot = await querier.GetStatusAsync();

            Assert.True(snapshot.CollectorOnline);
            Assert.Equal(0L, snapshot.DbSizeBytes);
            Assert.Equal(0L, snapshot.TraceCount);
            Assert.Equal(0L, snapshot.SpanCount);
        }
        finally
        {
            try { Directory.Delete(missingDir, recursive: true); } catch { }
        }
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
    public async Task GetStatusAsync_ReportsCountsAndFileSize()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "svc", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("t3", "svc", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);
        SeedSpan("t1", "s1", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z");
        SeedSpan("t1", "s2", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z");
        SeedSpan("t2", "s1", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z");

        var snapshot = await _querier.GetStatusAsync();

        Assert.Equal(3L, snapshot.TraceCount);
        Assert.True(snapshot.SpanCount >= 3L);
        Assert.True(snapshot.DbSizeBytes > 0L);
    }

    [Fact]
    public async Task ExecuteRawQuery_SelectAllRows_ReturnsDictionaries()
    {
        SeedTrace("t1", "svc-a", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 2);
        SeedTrace("t2", "svc-b", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 5);

        var rows = await _querier.ExecuteRawQuery(
            $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} ORDER BY {OtelDb.TracesStartTimeColumn}");

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => Equals(r[OtelDb.TracesServiceNameColumn], "svc-a") && Equals(r[OtelDb.TracesSpanCountColumn], 2L));
        Assert.Contains(rows, r => Equals(r[OtelDb.TracesServiceNameColumn], "svc-b") && Equals(r[OtelDb.TracesSpanCountColumn], 5L));
    }

    [Fact]
    public async Task ExecuteRawQuery_AggregateCount_ReturnsSingleRow()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "svc", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);

        var rows = await _querier.ExecuteRawQuery($"SELECT COUNT(*) AS total FROM {OtelDb.TracesTable}");

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(2L, row["total"]);
    }

    [Fact]
    public async Task ExecuteRawQuery_NullCell_BecomesNullInDictionary()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);

        var rows = await _querier.ExecuteRawQuery(
            $"SELECT {OtelDb.TracesTraceIdColumn}, NULL AS maybe FROM {OtelDb.TracesTable}");

        Assert.Single(rows);
        Assert.Null(rows[0]["maybe"]);
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
    public async Task ExecuteRawQuery_CannotBypassReadOnlyWithInsert()
    {
        // Even if ValidateSelectOnly were misconfigured, the read-only
        // connection should refuse this write. The keyword check is the
        // outer defense; the read-only mode is the ultimate authority.
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            await _querier.ExecuteRawQuery(
                "INSERT INTO traces (trace_id, service_name, start_time, end_time, span_count) VALUES ('x','y','2026-01-01T00:00:00Z','2026-01-01T00:00:01Z',1)");
        });
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

    private sealed class MockEnvironment : IEnvironmentVariableProvider
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? this[string variable]
        {
            get => _values.TryGetValue(variable, out var v) ? v : null;
            set
            {
                if (value is null) _values.Remove(variable);
                else _values[variable] = value;
            }
        }

        public string? GetEnvironmentVariable(string variable) => this[variable];

        public string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target) => this[variable];

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() =>
            new Dictionary<string, string>(_values, StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables(EnvironmentVariableTarget target) =>
            GetEnvironmentVariables();

        public string ExpandEnvironmentVariables(string name) => name;

        public void SetEnvironmentVariable(string variable, string? value) => this[variable] = value;

        public void SetEnvironmentVariable(string variable, string? value, EnvironmentVariableTarget target) =>
            this[variable] = value;
    }

    private sealed class PassthroughFileSystem : IFileSystem
    {
        public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        public string ReadAllText(string path) => File.ReadAllText(path);
    }
}