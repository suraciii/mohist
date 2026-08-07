using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Mohist.Server.TestSupport;
using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class OtelStorageGuardSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly OtelDb _db;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keeper;
    private readonly InMemoryServerFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly long _budgetBytes = 1_000;

    public OtelStorageGuardSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
    }

    public void Dispose()
    {
        _keeper.Dispose();
    }

    [Fact]
    public void Constructor_HighWatermark_IsNinetyPercentOfBudget()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.Equal(_budgetBytes, guard.BudgetBytes);
        Assert.Equal((long)(_budgetBytes * OtelStorageWatermarks.HighWatermarkRatio), guard.HighWatermarkBytes);
        Assert.Equal((long)(_budgetBytes * OtelStorageWatermarks.LowWatermarkRatio), guard.LowWatermarkBytes);
    }

    [Fact]
    public void Constructor_NoMarker_DefaultsToAdmissionClosed()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Constructor_MarkerSaysClosed_AdmitsAsClosed()
    {
        WriteMarker(new OtelStorageMarker(
            Version: OtelStorageGuard.MarkerVersion,
            AdmissionClosed: true,
            LastUsageBytes: 950,
            LastArbitration: Now));

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Constructor_MarkerSaysOpen_AdmitsAsOpen()
    {
        WriteMarker(new OtelStorageMarker(
            Version: OtelStorageGuard.MarkerVersion,
            AdmissionClosed: false,
            LastUsageBytes: 100,
            LastArbitration: Now));

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.False(guard.AdmissionClosed);
    }

    [Fact]
    public void Constructor_MarkerMissing_FallsBackToAdmissionClosed()
    {
        // _fileSystem is empty — no marker present.
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Constructor_CorruptMarker_FallsBackToAdmissionClosed()
    {
        _fileSystem.WriteAllText(_db.DatabasePath + OtelStorageGuard.MarkerSuffix, "not-json");

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Constructor_VersionMismatch_FallsBackToAdmissionClosed()
    {
        WriteMarker(new OtelStorageMarker(
            Version: "otel-storage-guard/v0",
            AdmissionClosed: false,
            LastUsageBytes: 100,
            LastArbitration: Now));

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_AboveHighWatermark_ClosesAdmission()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        var outcome = guard.Arbitrate((long)(_budgetBytes * 0.95));

        Assert.True(outcome.AdmissionClosed);
        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_BelowLowWatermark_OpensAdmission()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        var outcome = guard.Arbitrate((long)(_budgetBytes * 0.10));

        Assert.False(outcome.AdmissionClosed);
        Assert.False(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_BetweenWatermarks_PreservesCurrentState()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        // First drive above high to close.
        guard.Arbitrate((long)(_budgetBytes * 0.95));
        Assert.True(guard.AdmissionClosed);

        // Between watermarks but already closed: stays closed.
        guard.Arbitrate((long)(_budgetBytes * 0.85));
        Assert.True(guard.AdmissionClosed);

        // Below low watermark: opens.
        guard.Arbitrate((long)(_budgetBytes * 0.10));
        Assert.False(guard.AdmissionClosed);

        // Between watermarks but currently open: stays open.
        guard.Arbitrate((long)(_budgetBytes * 0.85));
        Assert.False(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_PersistsMarkerAfterArbitration()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        guard.Arbitrate((long)(_budgetBytes * 0.95));

        var persisted = guard.LastPersistedMarker;
        Assert.NotNull(persisted);
        Assert.True(persisted!.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_MarkerRoundTrip_PreservesAdmissionState()
    {
        var first = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);
        first.Arbitrate((long)(_budgetBytes * 0.95));

        var restarted = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.True(restarted.AdmissionClosed);
    }

    [Fact]
    public void Constructor_MarkerSaysOpen_DoesNotRederiveFromActualStoreSize()
    {
        // The marker is the seed; the actual store size is only
        // visible through a probe call. Seeding admission open from
        // the marker is fine — the maintenance loop's first probe
        // re-derives the watermark and closes admission if the
        // store is actually over budget.
        WriteMarker(new OtelStorageMarker(
            Version: OtelStorageGuard.MarkerVersion,
            AdmissionClosed: false,
            LastUsageBytes: 100,
            LastArbitration: Now));

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.False(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_RestoredIntoOverBudgetStore_ClosesAdmission()
    {
        // Marker says open (last run was healthy) but the on-disk
        // store has grown past the high watermark since the marker
        // was written. The first Arbitrate call (driven by the
        // maintenance loop's first probe) re-derives the
        // watermark and closes admission so the write path does
        // not silently accept writes as healthy.
        WriteMarker(new OtelStorageMarker(
            Version: OtelStorageGuard.MarkerVersion,
            AdmissionClosed: false,
            LastUsageBytes: 100,
            LastArbitration: Now));

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);
        Assert.False(guard.AdmissionClosed);

        guard.Arbitrate((long)(_budgetBytes * 0.95));

        Assert.True(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_RestoredIntoHealthyStore_OpensAfterFirstProbe()
    {
        // Marker says closed (last run was over budget), but the
        // on-disk store has since dropped well below the low
        // watermark. The first probe re-derives the watermark and
        // opens admission.
        WriteMarker(new OtelStorageMarker(
            Version: OtelStorageGuard.MarkerVersion,
            AdmissionClosed: true,
            LastUsageBytes: _budgetBytes,
            LastArbitration: Now));

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);
        Assert.True(guard.AdmissionClosed);

        guard.Arbitrate((long)(_budgetBytes * 0.10));

        Assert.False(guard.AdmissionClosed);
    }

    [Fact]
    public void Arbitrate_NegativeUsage_Throws()
    {
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() => guard.Arbitrate(-1));
    }

    [Fact]
    public void Arbitrate_PublishesAndClearsStorageBudgetExhaustedUnderRuntimeLock()
    {
        using var runtime = new RuntimeObservability(
            enabled: true,
            new RuntimeEpoch(Now),
            _timeProvider,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>());

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes, runtime);

        guard.Arbitrate((long)(_budgetBytes * 0.95));
        Assert.Equal(
            RuntimeDegradationCodes.StorageBudgetExhausted,
            runtime.GetSnapshot().LatestDegradation!.Code);
        Assert.True(runtime.HasActiveDegradation(DegradationSource.IngestProtection));

        guard.Arbitrate((long)(_budgetBytes * 0.10));
        Assert.False(runtime.HasActiveDegradation(DegradationSource.IngestProtection));
    }

    [Fact]
    public void Arbitrate_ReclamationBlocked_ClosesAdmissionBelowHighWatermark()
    {
        using var runtime = new RuntimeObservability(
            enabled: true,
            new RuntimeEpoch(Now),
            _timeProvider,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>());

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes, runtime);
        // Open first.
        guard.Arbitrate((long)(_budgetBytes * 0.10));
        Assert.False(guard.AdmissionClosed);

        // Blocked checkpoint forces close even though usage was low.
        guard.Arbitrate((long)(_budgetBytes * 0.50), reclamationBlocked: true);

        Assert.True(guard.AdmissionClosed);
        Assert.True(runtime.HasActiveDegradation(DegradationSource.IngestProtection));
    }

    [Fact]
    public void Arbitrate_StorageBudgetExhaustedDominatesOtherIngestProtectionReason()
    {
        // Publish a generic TelemetryRejected reason first (e.g. a
        // prior batch was rejected for malformed payloads). When
        // the guard then reports storage budget exhausted, the more
        // specific reason must dominate latest_degradation because
        // both share the same source under the same lock.
        using var runtime = new RuntimeObservability(
            enabled: true,
            new RuntimeEpoch(Now),
            _timeProvider,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>());

        runtime.RecordIngest(IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(0, 1, 0, 0),
            IngestWriteResult.NotAttempted()));
        Assert.Equal(
            RuntimeDegradationCodes.TelemetryRejected,
            runtime.GetSnapshot().LatestDegradation!.Code);

        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes, runtime);
        guard.Arbitrate((long)(_budgetBytes * 0.95));

        Assert.Equal(
            RuntimeDegradationCodes.StorageBudgetExhausted,
            runtime.GetSnapshot().LatestDegradation!.Code);
    }

    [Fact]
    public void RecordIngest_WhileAdmissionClosed_PreservesStorageBudgetReason()
    {
        using var runtime = new RuntimeObservability(
            enabled: true,
            new RuntimeEpoch(Now),
            _timeProvider,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>());
        var guard = new OtelStorageGuard(_db, _fileSystem, _timeProvider, _budgetBytes, runtime);
        guard.Arbitrate((long)(_budgetBytes * 0.95));

        runtime.RecordIngest(IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(0, 3, 0, 0),
            IngestWriteResult.NotAttempted()));

        var snapshot = runtime.GetSnapshot();
        Assert.Equal(RuntimeState.Degraded, snapshot.Status);
        Assert.Equal(RuntimeDegradationCodes.StorageBudgetExhausted, snapshot.LatestDegradation!.Code);
        Assert.Equal(3, snapshot.Telemetry.RejectedSpans);
    }

    private void WriteMarker(OtelStorageMarker marker)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            marker,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        _fileSystem.WriteAllText(_db.DatabasePath + OtelStorageGuard.MarkerSuffix, json);
    }

    private sealed record OtelStorageMarker(
        string Version,
        bool AdmissionClosed,
        long LastUsageBytes,
        DateTimeOffset LastArbitration);
}
