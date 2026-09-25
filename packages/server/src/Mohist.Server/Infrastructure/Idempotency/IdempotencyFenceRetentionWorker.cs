using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.Infrastructure.Idempotency;

/// <summary>
/// Bounds the request fence's storage: finished fence rows stop replaying a
/// recorded decision once they age past <see cref="RetentionWindow"/> and are
/// removed. The window is deliberately longer than any retry horizon a caller
/// can rely on — a key replayed within it returns the first recorded outcome,
/// a key replayed after it is treated as a new request.
/// </summary>
public sealed class IdempotencyFenceRetentionWorker : BackgroundService
{
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IdempotencyFenceRetentionWorker> _logger;

    public IdempotencyFenceRetentionWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<IdempotencyFenceRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one cleanup pass. This is public so hosting and deterministic
    /// specs can exercise the exact pass used by the background loop without
    /// waiting for its hourly interval.
    /// </summary>
    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var fence = scope.ServiceProvider.GetRequiredService<IdempotencyFence>();
            var cutoff = _timeProvider.GetUtcNow() - RetentionWindow;
            return await fence.PruneFinishedBeforeAsync(cutoff, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            // Retention is housekeeping, not a reason to terminate the Server.
            // The next scheduled pass retries the same cutoff computation.
            _logger.LogWarning(ex, "Failed to prune finished request fence rows");
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DelayAsync(InitialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await PruneExpiredAsync(stoppingToken);
            await DelayAsync(Interval, stoppingToken);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
