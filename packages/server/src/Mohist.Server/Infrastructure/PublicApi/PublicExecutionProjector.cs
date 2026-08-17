using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// Tuning knobs for the public execution projector's hosted loop. All
/// of them affect latency and database politeness only; projection
/// correctness is checkpoint-driven.
/// </summary>
public sealed class PublicProjectionOptions
{
    /// <summary>Upper bound of the timer sweep between nudges. Default 5 seconds.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Pause between back-to-back batches while catching up. Default 50 ms.</summary>
    public TimeSpan BatchPause { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Retry attempts for transient SQLite single-writer contention. Default 5.</summary>
    public int BusyRetryLimit { get; set; } = 5;

    /// <summary>Backoff multiplier per retry under write contention. Default 100 ms.</summary>
    public TimeSpan BusyRetryBackoff { get; set; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// The hosted public execution projector: the single background writer
/// of the public projection tables. It waits on the nudge channel for
/// latency and on a timer sweep as the safety net, and runs checkpoint
/// batches through <see cref="PublicApiProjectionEngine"/> until the
/// projection catches up. Transient SQLite single-writer contention is
/// retried with a small backoff; the batches stay small on purpose.
/// </summary>
public sealed class PublicExecutionProjector : BackgroundService
{
    private readonly PublicApiProjectionEngine _engine;
    private readonly PublicProjectionNudge _nudge;
    private readonly PublicProjectionOptions _options;
    private readonly ILogger<PublicExecutionProjector> _log;

    public PublicExecutionProjector(
        PublicApiProjectionEngine engine,
        PublicProjectionNudge nudge,
        IOptions<PublicProjectionOptions> options,
        ILogger<PublicExecutionProjector> log)
    {
        _engine = engine;
        _nudge = nudge;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForNudgeOrSweepAsync(stoppingToken);
                await DrainAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The public execution projector stopped unexpectedly");
            throw;
        }
    }

    private async Task WaitForNudgeOrSweepAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.SweepInterval);
            await _nudge.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timer sweep elapsed without a nudge.
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunBatchWithBusyRetryAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "A public projection batch failed; the next sweep retries from the checkpoint");
                return;
            }

            if (!worked)
            {
                return;
            }

            await DelayAsync(_options.BatchPause, ct);
        }
    }

    private async Task<bool> RunBatchWithBusyRetryAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _engine.ProcessPendingAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt < _options.BusyRetryLimit && IsBusy(ex))
            {
                _log.LogDebug(
                    "Public projection batch hit SQLite write contention (attempt {Attempt}); retrying",
                    attempt + 1);
                await DelayAsync(TimeSpan.FromTicks(_options.BusyRetryBackoff.Ticks * (attempt + 1)), ct);
            }
        }
    }

    private static bool IsBusy(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
        && (sqlite.SqliteErrorCode == 5 || sqlite.SqliteErrorCode == 6);

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
