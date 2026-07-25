using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class OtelStorageMaintenanceSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly OtelDb _db;
    private readonly SqliteConnection _keeper;
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly InMemoryServerFileSystem _fileSystem = new();
    private readonly List<string> _statements = new();
    private readonly long _budgetBytes = 1_000;

    public OtelStorageMaintenanceSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
    }

    public void Dispose()
    {
        _keeper.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_BelowHighWatermark_PerformsNoEvictionOrReclaim()
    {
        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 400 }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, guard.LastPersistedMarker is null ? 1 : 0); // marker is persisted
        Assert.False(guard.AdmissionClosed);
        // No SELECT/DELETE/PRAGMA eviction or reclaim statements.
        Assert.DoesNotContain(_statements, sql => sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_statements, sql => sql.Contains("incremental_vacuum", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_statements, sql => sql.Contains("wal_checkpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_AboveHighWatermark_DeletesOldestCompleteTracesUntilBelowLowWatermark()
    {
        // Seed 2 batches of complete Traces plus a few survivors.
        // The probe sequence drives the loop through two
        // deletion iterations: the first drops BatchSize and the
        // probe still reports above the low watermark, so the
        // loop iterates again; the second drops another BatchSize
        // and the probe now reports below the low watermark, so
        // the loop exits. Survivors are untouched.
        var batchSize = OtelStorageMaintenance.BatchSize;
        var total = (batchSize * 2) + 3;
        for (var i = 0; i < total; i++)
        {
            SeedCompleteTrace($"aged-{i:D4}", startOffsetHours: 200 + i, endOffsetHours: 100 + i);
            SeedSpan($"aged-{i:D4}", "s1", startHoursAgo: 200 + i, endHoursAgo: 100 + i);
        }

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[]
        {
            950, // initial probe → above high watermark
            870, // after first deletion → still above low watermark
            750, // after second deletion → below low watermark
        }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        // The survivors are the 3 most recently started traces
        // (highest startOffsetHours). Eviction deletes oldest
        // first (lowest startOffsetHours), so the survivors are
        // the last 3 we seeded.
        for (var i = total - (batchSize * 2); i < total; i++)
            Assert.Equal(0, CountTraces(_db, $"aged-{i:D4}"));
        for (var i = 0; i < total - (batchSize * 2); i++)
            Assert.Equal(1, CountTraces(_db, $"aged-{i:D4}"));

        // The probe ended below the low watermark, so admission
        // must be open.
        Assert.False(guard.AdmissionClosed);
    }

    [Fact]
    public async Task ExecuteAsync_NoRemovableTrace_ClosesAdmissionAndSignalsReclamationBlocked()
    {
        // All traces are still being collected (end_time in the
        // future). Eviction cannot reduce usage below the low
        // watermark, so admission must close.
        SeedCompleteTrace("growing", startOffsetHours: 200, endOffsetHours: -10);
        SeedSpan("growing", "s1", startHoursAgo: 200, endHoursAgo: -10);

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[]
        {
            950, // initial probe → above high watermark
            950, // no removable trace → still over budget
        }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        Assert.True(guard.AdmissionClosed);
        Assert.Equal(1, CountTraces(_db, "growing"));
    }

    [Fact]
    public async Task ExecuteAsync_IssuesIncrementalVacuumAndTruncatingCheckpoint()
    {
        SeedCompleteTrace("t1", startOffsetHours: 200, endOffsetHours: 100);
        SeedSpan("t1", "s1", startHoursAgo: 200, endHoursAgo: 100);

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 950, 870, 750 }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        Assert.Contains(_statements, sql =>
            sql.Contains("PRAGMA incremental_vacuum", StringComparison.OrdinalIgnoreCase)
            && sql.Contains(OtelDb.IncrementalVacuumPages.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Contains(_statements, sql =>
            sql.Contains("wal_checkpoint(TRUNCATE)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_NeverIssuesFullVacuum()
    {
        SeedCompleteTrace("t1", startOffsetHours: 200, endOffsetHours: 100);

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 950, 870, 750 }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        foreach (var sql in _statements)
        {
            // SQLite's full VACUUM is the only form that mutates
            // the .db file beyond incremental reclaim + checkpoint.
            // We forbid any statement whose first non-whitespace
            // token is VACUUM, including VACUUM INTO.
            var trimmed = sql.TrimStart();
            Assert.False(
                trimmed.StartsWith("VACUUM", StringComparison.OrdinalIgnoreCase),
                $"Full VACUUM not allowed: {sql}");
            Assert.DoesNotMatch(@"(?i)^VACUUM(?:\s|;|$)", trimmed);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BoundedBatch_OneBatchPerIteration()
    {
        // Seed BatchSize + 5 aged traces; expect the first pass to
        // delete exactly BatchSize of them and stop the inner loop
        // because the probe now reports below the low watermark.
        var total = OtelStorageMaintenance.BatchSize + 5;
        for (var i = 0; i < total; i++)
        {
            var id = $"aged-{i:D4}";
            SeedCompleteTrace(id, startOffsetHours: 200 + i, endOffsetHours: 100 + i);
            SeedSpan(id, "s1", startHoursAgo: 200 + i, endHoursAgo: 100 + i);
        }

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[]
        {
            950, // initial probe → above high watermark
            750, // after BatchSize deletions → below low watermark
        }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        Assert.Equal(total - OtelStorageMaintenance.BatchSize, CountAllTraces(_db));
    }

    [Fact]
    public async Task ExecuteAsync_StatementCount_IndependentOfUnrelatedHistory()
    {
        // Same aged-count workload, different sizes of unrelated
        // (in-window) history. Both must yield the same statement
        // count because the eviction is index-bounded and never
        // scans the unrelated table.
        var withLittle = RunStatementCount(unrelated: 0);
        var withMuch = RunStatementCount(unrelated: 1_000);

        Assert.Equal(withLittle, withMuch);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsMarkerAfterArbitration()
    {
        SeedCompleteTrace("t1", startOffsetHours: 200, endOffsetHours: 100);

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 950, 870, 750 }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        await maintenance.ExecuteAsync(CancellationToken.None);

        var persisted = guard.LastPersistedMarker;
        Assert.NotNull(persisted);
        Assert.False(persisted!.AdmissionClosed);
    }

    [Fact]
    public async Task ExecuteAsync_ReclaimYieldsOnBusyCheckpoint_DoesNotThrow()
    {
        SeedCompleteTrace("t1", startOffsetHours: 200, endOffsetHours: 100);

        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 950, 870 }),
            probeExhaustsTo: 500);
        var guard = NewGuard(probe);
        var maintenance = new OtelStorageMaintenance(
            _db,
            probe,
            guard,
            _timeProvider,
            new BlockedReclaimer(),
            new OtelOptions { StorageBudgetBytes = _budgetBytes },
            _statements.Add);

        await maintenance.ExecuteAsync(CancellationToken.None);

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public async Task ExecuteAsync_CompletesWithoutWaitingForExternalSignal()
    {
        var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 100 }));
        var guard = NewGuard(probe);
        var maintenance = NewMaintenance(probe, guard);

        var execution = maintenance.ExecuteAsync(CancellationToken.None);

        await execution;
        Assert.True(execution.IsCompletedSuccessfully);
    }

    private int RunStatementCount(int unrelated)
    {
        var (db, keeper) = InMemoryOtelDb.Create();
        try
        {
            SeedCompleteTraceTo(db, "aged", startOffsetHours: 200, endOffsetHours: 100);
            SeedSpanTo(db, "aged", "s1", startHoursAgo: 200, endHoursAgo: 100);
            for (var i = 0; i < unrelated; i++)
                SeedInWindowTraceTo(db, $"fresh-{i:D4}");

            var statements = new List<string>();
            var probe = new ScriptedProbe(usageByCall: new Queue<long>(new long[] { 950, 750 }));
            var fs = new InMemoryServerFileSystem();
            var guard = new OtelStorageGuard(db, fs, _timeProvider, _budgetBytes);
            var maintenance = new OtelStorageMaintenance(
                db, probe, guard, _timeProvider,
                new SqliteOtelStorageReclaimer(),
                new OtelOptions { StorageBudgetBytes = _budgetBytes },
                statements.Add);
            maintenance.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
            return statements.Count;
        }
        finally
        {
            keeper.Dispose();
        }
    }

    private OtelStorageGuard NewGuard(ScriptedProbe probe)
    {
        _ = probe;
        return new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);
    }

    private OtelStorageMaintenance NewMaintenance(ScriptedProbe probe, OtelStorageGuard guard)
    {
        return new OtelStorageMaintenance(
            _db,
            probe,
            guard,
            _timeProvider,
            new SqliteOtelStorageReclaimer(),
            new OtelOptions { StorageBudgetBytes = _budgetBytes },
            _statements.Add);
    }

    private void SeedCompleteTrace(string traceId, int startOffsetHours, int endOffsetHours)
    {
        SeedCompleteTraceTo(_db, traceId, startOffsetHours, endOffsetHours);
    }

    private void SeedInWindowTrace(string traceId)
    {
        SeedInWindowTraceTo(_db, traceId);
    }

    private void SeedSpan(string traceId, string spanId, int startHoursAgo, int endHoursAgo)
    {
        SeedSpanTo(_db, traceId, spanId, startHoursAgo, endHoursAgo);
    }

    private static void SeedCompleteTraceTo(OtelDb db, string traceId, int startOffsetHours, int endOffsetHours)
    {
        SeedTraceTo(db, traceId, startTime: HoursAgo(startOffsetHours), endTime: HoursAgo(endOffsetHours));
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

    private static int CountTraces(OtelDb db, string traceId)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id;";
        cmd.Parameters.AddWithValue("$id", traceId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int CountSpans(OtelDb db, string traceId)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $id;";
        cmd.Parameters.AddWithValue("$id", traceId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int CountAllTraces(OtelDb db)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class BlockedReclaimer : IOtelStorageReclaimer
    {
        public bool Reclaim(OtelDb db, CancellationToken cancellationToken, Action<string>? statementObserver = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statementObserver?.Invoke("PRAGMA wal_checkpoint(TRUNCATE);");
            return true;
        }
    }

    private sealed class ScriptedProbe : IOtelStorageProbe
    {
        private readonly Queue<long> _usages;
        private readonly long? _exhaustTo;

        public ScriptedProbe(Queue<long> usageByCall, long? probeExhaustsTo = null)
        {
            _usages = usageByCall;
            _exhaustTo = probeExhaustsTo;
        }

        public StorageProbeMetadata Probe()
        {
            if (_usages.Count > 0)
                return new StorageProbeMetadata(_usages.Dequeue());
            return new StorageProbeMetadata(_exhaustTo ?? 0);
        }
    }
}
