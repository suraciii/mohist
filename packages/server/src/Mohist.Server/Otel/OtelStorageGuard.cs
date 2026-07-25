using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Otel;

/// <summary>
/// Single source of truth for whether the OTel ingestion path is
/// currently refusing writes because the storage budget cannot be
/// reclaimed fast enough. The guard is the only writer of the
/// persisted <c>.meta</c> sidecar marker; the maintenance callback
/// invokes <see cref="Arbitrate"/> after every size-budget eviction
/// pass and the budget-aware <see cref="IIngestProtectionDecision"/>
/// reads <see cref="AdmissionClosed"/> on every Span.
///
/// Marker semantics: the marker records the admission state the
/// guard observed at the end of the previous run, so a startup into
/// a still-over-budget store does not silently re-accept writes. A
/// missing or corrupt marker falls back to admission-closed until
/// the first maintenance probe re-derives the watermark from the
/// real probe, so correctness never depends on the marker
/// surviving.
/// </summary>
public sealed class OtelStorageGuard
{
    internal const string MarkerSuffix = ".meta";
    internal const string MarkerVersion = "otel-storage-guard/v1";

    private readonly object _gate = new();
    private readonly OtelDb _db;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeObservability? _runtime;
    private readonly long _budgetBytes;
    private readonly long _highWatermarkBytes;
    private readonly long _lowWatermarkBytes;
    private volatile bool _admissionClosed;
    private long _lastUsageBytes;
    private DateTimeOffset _lastArbitration;
    private OtelStorageMarker? _lastPersistedMarker;

    public OtelStorageGuard(
        OtelDb db,
        IFileSystem fileSystem,
        TimeProvider timeProvider,
        IOptions<OtelOptions> options,
        RuntimeObservability? runtime = null)
        : this(db, fileSystem, timeProvider, (options ?? throw new ArgumentNullException(nameof(options))).Value.StorageBudgetBytes, runtime)
    {
    }

    public OtelStorageGuard(
        OtelDb db,
        IFileSystem fileSystem,
        TimeProvider timeProvider,
        long storageBudgetBytes,
        RuntimeObservability? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(storageBudgetBytes);

        _db = db;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
        _runtime = runtime;
        _budgetBytes = storageBudgetBytes;
        _highWatermarkBytes = ComputeWatermark(storageBudgetBytes, OtelStorageWatermarks.HighWatermarkRatio);
        _lowWatermarkBytes = ComputeWatermark(storageBudgetBytes, OtelStorageWatermarks.LowWatermarkRatio);
        _admissionClosed = SeedAdmissionFromMarker();

        // Sync the runtime with the seeded admission state so the
        // first probe doesn't need to publish. Done under no lock
        // because no other thread has a reference to this instance
        // yet — DI constructs singletons serially.
        if (_admissionClosed)
            _runtime?.PublishStorageBudgetExhausted(true, at: _timeProvider.GetUtcNow());
    }

    /// <summary>Absolute path of the persisted <c>.meta</c> sidecar.</summary>
    public string MarkerPath => _db.DatabasePath + MarkerSuffix;

    /// <summary>Configured storage budget in bytes.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>High watermark (90% of budget) in bytes.</summary>
    public long HighWatermarkBytes => _highWatermarkBytes;

    /// <summary>Low watermark (80% of budget) in bytes.</summary>
    public long LowWatermarkBytes => _lowWatermarkBytes;

    /// <summary>
    /// Whether the ingestion path should currently refuse new
    /// writes. Read on every Span by the
    /// <see cref="IIngestProtectionDecision"/>; the read is a
    /// volatile load and never blocks.
    /// </summary>
    public bool AdmissionClosed => _admissionClosed;

    /// <summary>Snapshot of the last measured usage in bytes.</summary>
    public long LastUsageBytes
    {
        get { lock (_gate) return _lastUsageBytes; }
    }

    /// <summary>
    /// Updates admission based on the latest probe reading. Closes
    /// admission when usage is at or above the high watermark and
    /// reopens it once usage drops below the low watermark. Always
    /// publishes the resulting state to the runtime and persists a
    /// marker, so the runtime and the on-disk sidecar stay in
    /// sync with the latest probe reading.
    /// </summary>
    /// <param name="usageBytes">Combined db+WAL+SHM usage in bytes.</param>
    /// <returns>The arbitration outcome describing the resulting
    /// admission state and the watermarks it was compared against.</returns>
    public OtelStorageArbitration Arbitrate(long usageBytes, bool reclamationBlocked = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usageBytes);
        OtelStorageArbitration outcome;
        var now = _timeProvider.GetUtcNow();
        OtelStorageMarker marker;
        lock (_gate)
        {
            _lastUsageBytes = usageBytes;
            _lastArbitration = now;
            _admissionClosed = reclamationBlocked || ComputeAdmission(usageBytes, _admissionClosed);
            outcome = new OtelStorageArbitration(
                _admissionClosed,
                usageBytes,
                _highWatermarkBytes,
                _lowWatermarkBytes,
                now);
            marker = new OtelStorageMarker(
                MarkerVersion,
                _admissionClosed,
                usageBytes,
                now);
        }
        PublishLocked(_admissionClosed, now);
        PersistMarker(marker);
        return outcome;
    }

    private bool ComputeAdmission(long usageBytes, bool currentClosed)
    {
        if (usageBytes >= _highWatermarkBytes)
            return true;
        if (usageBytes < _lowWatermarkBytes)
            return false;
        return currentClosed;
    }

    private void PublishLocked(bool closed, DateTimeOffset now)
    {
        _runtime?.PublishStorageBudgetExhausted(closed, at: now);
    }

    private void PersistMarker(OtelStorageMarker marker)
    {
        try
        {
            var json = JsonSerializer.Serialize(marker, JSON.Options);
            _fileSystem.WriteAllText(MarkerPath, json);
            lock (_gate)
                _lastPersistedMarker = marker;
        }
        catch
        {
            // Marker write failure is non-fatal — the guard re-derives
            // admission from the next probe regardless.
        }
    }

    private bool SeedAdmissionFromMarker()
    {
        if (!_fileSystem.Exists(MarkerPath))
            return true;

        string? raw = null;
        try
        {
            raw = _fileSystem.ReadAllText(MarkerPath);
        }
        catch
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        try
        {
            var marker = JsonSerializer.Deserialize<OtelStorageMarker>(raw, JSON.Options);
            if (marker is null || !string.Equals(marker.Version, MarkerVersion, StringComparison.Ordinal))
                return true;
            _lastPersistedMarker = marker;
            return marker.AdmissionClosed;
        }
        catch
        {
            return true;
        }
    }

    internal OtelStorageMarker? LastPersistedMarker
    {
        get { lock (_gate) return _lastPersistedMarker; }
    }

    private static long ComputeWatermark(long budgetBytes, double ratio)
    {
        if (budgetBytes <= 0)
            return 0;
        var raw = budgetBytes * ratio;
        if (raw >= long.MaxValue)
            return long.MaxValue;
        return (long)raw;
    }
}

/// <summary>
/// Ratio constants used by the size eviction / admission
/// arbitration loop. Internal to the storage-budget feature so
/// operators cannot tune them — they are part of the bounded
/// design, not user preferences.
/// </summary>
public static class OtelStorageWatermarks
{
    public const double HighWatermarkRatio = 0.9;
    public const double LowWatermarkRatio = 0.8;
}

public readonly record struct OtelStorageArbitration(
    bool AdmissionClosed,
    long UsageBytes,
    long HighWatermarkBytes,
    long LowWatermarkBytes,
    DateTimeOffset At);

internal sealed record OtelStorageMarker(
    string Version,
    bool AdmissionClosed,
    long LastUsageBytes,
    DateTimeOffset LastArbitration);

internal static class OtelStorageMarkerJson
{
    public static readonly JsonSerializerOptions Options = JSON.Options;
}
