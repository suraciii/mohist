using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class OtelRetentionMaintenanceSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionAge = TimeSpan.FromHours(72);

    private readonly OtelDb _db;
    private readonly SqliteConnection _keeper;
    private readonly FakeTimeProvider _timeProvider;
    private readonly OtelRetentionMaintenance _maintenance;
    private readonly List<string> _statements;

    public OtelRetentionMaintenanceSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
        _timeProvider = new FakeTimeProvider(Now);
        _statements = new List<string>();
        _maintenance = new OtelRetentionMaintenance(
            _db,
            _timeProvider,
            RetentionAge,
            sql => _statements.Add(sql));
    }

    public void Dispose()
    {
        _keeper.Dispose();
    }

    [Fact]
    public async Task EnsureInitialized_CreatesIdxTracesEndIndex()
    {
        using var connection = _db.OpenReadWriteConnection();

        Assert.True(IndexExists(connection, OtelDb.TracesEndIndex));
    }

    [Fact]
    public async Task ExecuteAsync_AgedTrace_DeletesHeaderAndAllSpans()
    {
        SeedAgedTrace("aged-1", hourOffset: 0);
        SeedSpan("aged-1", "s1", startHoursAgo: 100, endHoursAgo: 78);
        SeedSpan("aged-1", "s2", startHoursAgo: 78, endHoursAgo: 73);

        await _maintenance.ExecuteAsync(CancellationToken.None);

        AssertRowCount(_db, OtelDb.TracesTable, expected: 0, traceId: "aged-1");
        AssertRowCount(_db, OtelDb.SpansTable, expected: 0, traceId: "aged-1");
    }

    [Fact]
    public async Task ExecuteAsync_InWindowTrace_IsPreserved()
    {
        SeedInWindowTrace("fresh");
        SeedSpan("fresh", "s1", startHoursAgo: 2, endHoursAgo: 1);

        await _maintenance.ExecuteAsync(CancellationToken.None);

        AssertRowCount(_db, OtelDb.TracesTable, expected: 1, traceId: "fresh");
        AssertRowCount(_db, OtelDb.SpansTable, expected: 1, traceId: "fresh");
    }

    [Fact]
    public async Task ExecuteAsync_TraceStillReceivingSpans_NotAgedOut()
    {
        // Start_time is well past retention age, but end_time is in
        // the window: this Trace is still collecting Spans and must not
        // be deleted.
        SeedTrace("growing", startTime: HoursAgo(80), endTime: HoursAgo(1));
        SeedSpan("growing", "s1", startHoursAgo: 80, endHoursAgo: 79);
        SeedSpan("growing", "s2", startHoursAgo: 2, endHoursAgo: 1);

        await _maintenance.ExecuteAsync(CancellationToken.None);

        AssertRowCount(_db, OtelDb.TracesTable, expected: 1, traceId: "growing");
        AssertRowCount(_db, OtelDb.SpansTable, expected: 2, traceId: "growing");
    }

    [Fact]
    public async Task ExecuteAsync_AgedTracesExceedBatch_OnePassDeletesOneBatchOldestFirst()
    {
        var totalAged = OtelRetentionMaintenance.BatchSize + 5;
        // hourOffset 0 = most recent start_time; hourOffset
        // (totalAged-1) = oldest start_time. Maintenance deletes the
        // BatchSize oldest: hourOffset 5..totalAged-1.
        for (var i = 0; i < totalAged; i++)
        {
            var id = $"aged-{i:D4}";
            SeedAgedTrace(id, hourOffset: i);
        }

        await _maintenance.ExecuteAsync(CancellationToken.None);

        // The BatchSize oldest (hourOffset 5..totalAged-1) are deleted.
        for (var i = totalAged - OtelRetentionMaintenance.BatchSize; i < totalAged; i++)
        {
            var id = $"aged-{i:D4}";
            AssertRowCount(_db, OtelDb.TracesTable, expected: 0, traceId: id);
        }
        // The 5 most-recent traces (hourOffset 0..4) are still present.
        for (var i = 0; i < totalAged - OtelRetentionMaintenance.BatchSize; i++)
        {
            var id = $"aged-{i:D4}";
            AssertRowCount(_db, OtelDb.TracesTable, expected: 1, traceId: id);
        }

        await _maintenance.ExecuteAsync(CancellationToken.None);

        for (var i = 0; i < totalAged - OtelRetentionMaintenance.BatchSize; i++)
        {
            var id = $"aged-{i:D4}";
            AssertRowCount(_db, OtelDb.TracesTable, expected: 0, traceId: id);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AgedTracesExceedBatch_ResumeDoesNotDuplicateWork()
    {
        var totalAged = OtelRetentionMaintenance.BatchSize + 3;
        for (var i = 0; i < totalAged; i++)
            SeedAgedTrace($"aged-{i:D4}", hourOffset: i);

        for (var pass = 0; pass < 5; pass++)
        {
            _statements.Clear();
            await _maintenance.ExecuteAsync(CancellationToken.None);
            if (CountTraces(_db) == 0)
                break;
        }

        Assert.Equal(0, CountTraces(_db));
        Assert.Equal(0, CountSpans(_db));
    }

    [Fact]
    public async Task ExecuteAsync_PreCancelled_LeavesAllTracesInPlace()
    {
        SeedAgedTrace("aged-1", hourOffset: 0);
        SeedSpan("aged-1", "s1", startHoursAgo: 100, endHoursAgo: 78);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _maintenance.ExecuteAsync(cts.Token));

        AssertRowCount(_db, OtelDb.TracesTable, expected: 1, traceId: "aged-1");
        AssertRowCount(_db, OtelDb.SpansTable, expected: 1, traceId: "aged-1");
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePasses_OldestFirst()
    {
        var totalAged = OtelRetentionMaintenance.BatchSize + 2;
        for (var i = 0; i < totalAged; i++)
            SeedAgedTrace($"aged-{i:D4}", hourOffset: i);

        await _maintenance.ExecuteAsync(CancellationToken.None);

        // First batch deleted the BatchSize oldest (hourOffset
        // (totalAged - BatchSize)..totalAged-1).
        for (var i = totalAged - OtelRetentionMaintenance.BatchSize; i < totalAged; i++)
            AssertRowCount(_db, OtelDb.TracesTable, expected: 0, traceId: $"aged-{i:D4}");
        // Two most-recent remain (hourOffset 0..1).
        for (var i = 0; i < totalAged - OtelRetentionMaintenance.BatchSize; i++)
            AssertRowCount(_db, OtelDb.TracesTable, expected: 1, traceId: $"aged-{i:D4}");

        await _maintenance.ExecuteAsync(CancellationToken.None);

        for (var i = 0; i < totalAged - OtelRetentionMaintenance.BatchSize; i++)
            AssertRowCount(_db, OtelDb.TracesTable, expected: 0, traceId: $"aged-{i:D4}");
        Assert.Equal(0, CountTraces(_db));
    }

    [Fact]
    public async Task ExecuteAsync_UsesFakeTimeProvider_NoWallClockElapsed()
    {
        SeedAgedTrace("aged-1", hourOffset: 0);

        // Advance FakeTimeProvider by 100 hours; no real time elapses.
        _timeProvider.Advance(TimeSpan.FromHours(100));

        await _maintenance.ExecuteAsync(CancellationToken.None);

        AssertRowCount(_db, OtelDb.TracesTable, expected: 0, traceId: "aged-1");
    }

    [Fact]
    public async Task ExecuteAsync_InWindowAfterAdvance_PreservesTrace()
    {
        // In-window trace (end_time 1h before original Now).
        SeedInWindowTrace("fresh");

        // Advance FakeTimeProvider; trace stays within new window.
        _timeProvider.Advance(TimeSpan.FromHours(70));

        await _maintenance.ExecuteAsync(CancellationToken.None);

        AssertRowCount(_db, OtelDb.TracesTable, expected: 1, traceId: "fresh");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationMidPass_PreCancelledRollsBackAndNextPassResumes()
    {
        var totalAged = OtelRetentionMaintenance.BatchSize + 5;
        for (var i = 0; i < totalAged; i++)
            SeedAgedTrace($"aged-{i:D4}", hourOffset: i);

        // Pre-cancelled: the very first invocation never touches the DB.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _maintenance.ExecuteAsync(cts.Token));

        Assert.Equal(totalAged, CountTraces(_db));

        // Resume: the next uncancelled invocation deletes one batch.
        await _maintenance.ExecuteAsync(CancellationToken.None);

        Assert.Equal(totalAged - OtelRetentionMaintenance.BatchSize, CountTraces(_db));
    }

    [Fact]
    public async Task ExecuteAsync_NoAgedTraces_IssuesOnlyOneStatement()
    {
        SeedInWindowTrace("fresh");

        await _maintenance.ExecuteAsync(CancellationToken.None);

        Assert.Single(_statements);
    }

    [Fact]
    public async Task ExecuteAsync_BoundedBatch_IssuesThreeStatements()
    {
        SeedAgedTrace("aged-1", hourOffset: 0);
        SeedSpan("aged-1", "s1", startHoursAgo: 100, endHoursAgo: 78);

        await _maintenance.ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, _statements.Count);
    }

    [Fact]
    public async Task ExecuteAsync_StatementCount_IndependentOfUnrelatedHistory()
    {
        var withLittleHistory = RunMaintenanceWithFreshDb(unrelated: 0, agedOffset: 0);
        var withMuchHistory = RunMaintenanceWithFreshDb(unrelated: 1000, agedOffset: 0);

        Assert.Equal(withLittleHistory, withMuchHistory);
    }

    [Fact]
    public async Task ExecuteAsync_StatementCount_ScalesWithBatchNotAgedHistory()
    {
        // Same aged count (one batch), but varying amounts of
        // unrelated history — both must yield the same statement count
        // because the deletion is index-bounded.
        var withMuchAgedHistory = RunMaintenanceWithFreshDb(unrelated: 500, agedOffset: 0);
        var withLittleAgedHistory = RunMaintenanceWithFreshDb(unrelated: 0, agedOffset: 0);

        Assert.Equal(withMuchAgedHistory, withLittleAgedHistory);
    }

    [Fact]
    public async Task ExecuteAsync_NoCountStarOverTracesOrSpans()
    {
        SeedAgedTrace("aged", hourOffset: 0);
        SeedSpan("aged", "s1", startHoursAgo: 100, endHoursAgo: 78);
        for (var i = 0; i < 50; i++)
            SeedInWindowTrace($"fresh-{i:D4}");

        await _maintenance.ExecuteAsync(CancellationToken.None);

        Assert.All(_statements, sql =>
        {
            Assert.DoesNotContain("COUNT(*)", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("count(*)", sql, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task OtelOptions_RetentionMaxAge_DefaultsTo72Hours()
    {
        var options = new OtelOptions();

        Assert.Equal(TimeSpan.FromHours(72), options.RetentionMaxAge);
    }

    [Fact]
    public void OtelOptions_RetentionMaxAge_BindsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:RetentionMaxAge"] = "01:30:00",
            })
            .Build();

        var bound = config.GetSection(OtelOptions.SectionName).Get<OtelOptions>() ?? new OtelOptions();

        Assert.Equal(TimeSpan.FromMinutes(90), bound.RetentionMaxAge);
    }

    private int RunMaintenanceWithFreshDb(int unrelated, int agedOffset)
    {
        var (localDb, localKeeper) = InMemoryOtelDb.Create();
        try
        {
            SeedAgedTraceTo(localDb, "aged", agedOffset);
            SeedSpanTo(localDb, "aged", "s1", startHoursAgo: 100 + agedOffset, endHoursAgo: 78 + agedOffset);
            for (var i = 0; i < unrelated; i++)
                SeedInWindowTraceTo(localDb, $"fresh-{i:D4}");

            var captured = new List<string>();
            var maintenance = new OtelRetentionMaintenance(localDb, _timeProvider, RetentionAge, captured.Add);
            maintenance.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
            return captured.Count;
        }
        finally
        {
            localKeeper.Dispose();
        }
    }

    private void SeedAgedTrace(string traceId, int hourOffset) =>
        SeedAgedTraceTo(_db, traceId, hourOffset);

    private void SeedInWindowTrace(string traceId) =>
        SeedInWindowTraceTo(_db, traceId);

    private void SeedTrace(string traceId, string startTime, string endTime) =>
        SeedTraceTo(_db, traceId, startTime, endTime);

    private void SeedSpan(string traceId, string spanId, int startHoursAgo, int endHoursAgo) =>
        SeedSpanTo(_db, traceId, spanId, startHoursAgo, endHoursAgo);

    private static void SeedAgedTraceTo(OtelDb db, string traceId, int hourOffset)
    {
        // end_time well past the 72h cutoff; start_time distinctly older
        // so the batch order by start_time ASC is deterministic and the
        // hourOffset separates them by an hour each.
        SeedTraceTo(db, traceId, startTime: HoursAgo(200 + hourOffset), endTime: HoursAgo(80 + hourOffset));
    }

    private static void SeedInWindowTraceTo(OtelDb db, string traceId)
    {
        SeedTraceTo(db, traceId, startTime: HoursAgo(2), endTime: HoursAgo(1));
    }

    private static void SeedTraceTo(OtelDb db, string traceId, string startTime, string endTime)
    {
        using var connection = db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {OtelDb.TracesTable} (
                {OtelDb.TracesTraceIdColumn},
                {OtelDb.TracesServiceNameColumn},
                {OtelDb.TracesStartTimeColumn},
                {OtelDb.TracesEndTimeColumn},
                {OtelDb.TracesSpanCountColumn}
            ) VALUES ($trace_id, 'svc', $start_time, $end_time, 0);
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$start_time", startTime);
        cmd.Parameters.AddWithValue("$end_time", endTime);
        cmd.ExecuteNonQuery();
    }

    private static void SeedSpanTo(OtelDb db, string traceId, string spanId, int startHoursAgo, int endHoursAgo)
    {
        using var connection = db.OpenReadWriteConnection();
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
            ) VALUES ($trace_id, $span_id, 'op', 1, $start_time, $end_time, 0);
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$span_id", spanId);
        cmd.Parameters.AddWithValue("$start_time", HoursAgo(startHoursAgo));
        cmd.Parameters.AddWithValue("$end_time", HoursAgo(endHoursAgo));
        cmd.ExecuteNonQuery();
    }

    private static string HoursAgo(int hours)
    {
        var instant = Now.AddHours(-hours);
        return instant.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    private static bool IndexExists(SqliteConnection connection, string indexName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", indexName);
        return cmd.ExecuteScalar() is not null;
    }

    private static void AssertRowCount(OtelDb db, string table, long expected, string traceId)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {OtelDb.TracesTraceIdColumn} = $id;";
        cmd.Parameters.AddWithValue("$id", traceId);
        var actual = (long)cmd.ExecuteScalar()!;
        Assert.True(
            actual == expected,
            $"expected {expected} row(s) in {table} for trace {traceId}, got {actual}");
    }

    private static long CountTraces(OtelDb db)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable};";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long CountSpans(OtelDb db)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable};";
        return (long)cmd.ExecuteScalar()!;
    }
}
