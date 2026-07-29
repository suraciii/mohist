using Microsoft.Extensions.Logging;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Otel;

/// <summary>
/// Startup recovery callback for the observation store. Runs once
/// on the maintenance loop's first post-start tick: if the storage
/// probe reports combined <c>otel.db</c> + WAL + SHM usage strictly
/// above 100% of <see cref="OtelOptions.StorageBudgetBytes"/> — so
/// online eviction always gets the first chance to reclaim before
/// the rebuild kicks in — the recovery path
/// rebuilds an empty observation database in place. Rebuild work is
/// bounded by file-length reads, a connection-pool clear, and four
/// file deletions; it does not scan or iterate any Trace or Span
/// rows, so the start-up cost is independent of how much history
/// the oversized database holds.
///
/// Out-of-budget rebuild sequence:
/// <list type="number">
///   <item>drain pooled SQLite connections via <see cref="IOtelDbPool"/>,</item>
///   <item>reset <see cref="OtelDb"/> initialization so the next open
///     re-runs <c>EnsureInitialized</c>,</item>
///   <item>delete the old <c>.db</c>, <c>-wal</c>, <c>-shm</c>, and
///     <c>.meta</c> files through <see cref="IFileSystem"/>,</item>
///   <item>open a fresh read-write connection (which recreates the
///     schema with <c>auto_vacuum = INCREMENTAL</c>),</item>
///   <item>re-arbitrate admission against the fresh empty store so the
///     write path reopens and a stale <c>storage_budget_exhausted</c>
///     reason clears, and</item>
///   <item>publish the <c>storage_data_reset</c> degradation code on
///     the <c>StorageWrite</c> source plus one structured log.</item>
/// </list>
///
/// The data-reset reason is "write-unverified until the first
/// committed production write" and is cleared by the existing
/// <see cref="RuntimeObservability.RecordIngest"/> path when a real
/// write commits through <c>TraceIngester</c>. A rebuild failure is
/// contained to observation — it publishes the data-reset reason so
/// operators can see it and logs an error — and never touches or
/// locks the business database, so the core Server remains reachable.
/// </summary>
public sealed class OtelStorageRecoveryMaintenance : IOtelMaintenanceCallback
{
    public static readonly EventId DataResetEvent = new(43703, "OtelStorageDataReset");

    private readonly OtelDb _db;
    private readonly IOtelStorageProbe _probe;
    private readonly OtelStorageGuard _guard;
    private readonly IOtelDbPool _pool;
    private readonly IFileSystem _fileSystem;
    private readonly RuntimeObservability? _runtime;
    private readonly ILogger<OtelStorageRecoveryMaintenance>? _logger;
    private readonly Action<OtelStorageRecoveryOutcome>? _observer;
    private int _hasRun;

    public OtelStorageRecoveryMaintenance(
        OtelDb db,
        IOtelStorageProbe probe,
        OtelStorageGuard guard,
        IOtelDbPool pool,
        IFileSystem fileSystem,
        RuntimeObservability? runtime = null,
        ILogger<OtelStorageRecoveryMaintenance>? logger = null,
        Action<OtelStorageRecoveryOutcome>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _db = db;
        _probe = probe;
        _guard = guard;
        _pool = pool;
        _fileSystem = fileSystem;
        _runtime = runtime;
        _logger = logger;
        _observer = observer;
    }

    /// <summary>
    /// Resets the one-shot gate so the next invocation re-runs the
    /// recovery decision. Test-only — production lets the callback
    /// run exactly once per Server lifetime.
    /// </summary>
    internal void ResetFirstTickGateForTesting() => Interlocked.Exchange(ref _hasRun, 0);

    /// <summary>True after the recovery decision has been recorded.</summary>
    internal bool HasRun => Volatile.Read(ref _hasRun) != 0;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _hasRun, 1, 0) != 0)
            return Task.CompletedTask;

        try
        {
            var initial = _probe.Probe();
            if (initial.UsageBytes <= _guard.BudgetBytes)
            {
                _observer?.Invoke(new OtelStorageRecoveryOutcome(
                    OtelStorageRecoveryDecision.SkippedWithinBudget,
                    initial.UsageBytes));
                return Task.CompletedTask;
            }

            // Bounded recovery: out-of-task to keep this light on the
            // maintenance loop's first-tick budget.
            return Task.Run(() => Rebuild(initial.UsageBytes), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _hasRun, 0);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OTel storage recovery probe failed; skipping rebuild.");
            _observer?.Invoke(new OtelStorageRecoveryOutcome(
                OtelStorageRecoveryDecision.ProbeFailed,
                UsageBytes: -1,
                Failure: ex));
            return Task.CompletedTask;
        }
    }

    private void Rebuild(long usageBytes)
    {
        try
        {
            _pool.ClearAll();
            _db.ResetInitialization();
            foreach (var path in _db.ObservationStoreFiles())
                DeleteFile(path);

            using var connection = _db.OpenReadWriteConnection();

            // The rebuild replaced an oversized, unreclaimable store
            // with a fresh empty one. Re-arbitrate admission against
            // the new store so the write path opens immediately and a
            // stale storage_budget_exhausted reason clears — otherwise
            // the store is reported over-budget for up to a tick
            // despite being empty, and the first post-rebuild write is
            // refused. A probe failure here is non-fatal: the rebuild
            // itself succeeded, and the next maintenance tick
            // re-derives the watermark from the storage callback.
            ReArbitrateAfterRebuild();

            _logger?.LogWarning(
                DataResetEvent,
                "OTel observation data reset at startup: combined usage {UsageBytes}B exceeded the {BudgetBytes}B storage budget; the prior store was discarded.",
                usageBytes,
                _guard.BudgetBytes);
            _runtime?.PublishStorageDataReset(true);
            _observer?.Invoke(new OtelStorageRecoveryOutcome(
                OtelStorageRecoveryDecision.Rebuilt,
                usageBytes));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OTel storage recovery rebuild failed; the observation store is unusable until restart.");
            _runtime?.PublishStorageDataReset(true);
            _observer?.Invoke(new OtelStorageRecoveryOutcome(
                OtelStorageRecoveryDecision.RebuildFailed,
                usageBytes,
                Failure: ex));
        }
    }

    private void ReArbitrateAfterRebuild()
    {
        try
        {
            _guard.Arbitrate(_probe.Probe().UsageBytes);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "OTel storage recovery could not re-probe after rebuild; admission will be re-derived on the next maintenance tick.");
        }
    }

    private void DeleteFile(string path)
    {
        try
        {
            _fileSystem.Delete(path);
        }
        catch
        {
            // IFileSystem.Delete is documented to swallow missing
            // files. Other exceptions (permissions, etc.) are
            // best-effort here — the rebuild is at-most-once and the
            // publish of storage_data_reset already tells operators
            // that the store is being rebuilt; if a file truly
            // cannot be removed the rebuild will surface that through
            // OpenReadWriteConnection.
        }
    }
}

public enum OtelStorageRecoveryDecision
{
    SkippedWithinBudget,
    ProbeFailed,
    Rebuilt,
    RebuildFailed,
}

public sealed record OtelStorageRecoveryOutcome(
    OtelStorageRecoveryDecision Decision,
    long UsageBytes,
    Exception? Failure = null);
