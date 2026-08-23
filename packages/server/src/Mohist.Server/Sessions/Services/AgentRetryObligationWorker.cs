using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Resumes committed retry receipts that were still pending when the process
/// stopped and removes finished receipts after their bounded retention period.
/// Each pass is isolated in a scope and each receipt is independent: one
/// broken dispatch must not prevent another receipt from being resumed.
/// </summary>
public sealed class AgentRetryObligationWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRetryObligationWorker> _logger;

    public AgentRetryObligationWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AgentRetryObligationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one recovery and cleanup pass. This is public so hosting and
    /// deterministic integration specs can exercise the exact pass used by
    /// the background loop without waiting for its minute interval.
    /// </summary>
    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var operations = scope.ServiceProvider.GetRequiredService<AgentRetryOperationStore>();
            var retries = scope.ServiceProvider.GetRequiredService<AgentSessionRetryService>();
            var pending = await operations.ListPendingAsync(cancellationToken);

            foreach (var operation in pending)
            {
                try
                {
                    await retries.DispatchPendingAsync(
                        operation.ProjectId,
                        operation.OperationId,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Leave the receipt Pending. The next pass will retry the
                    // same pre-allocated dispatch identity.
                    _logger.LogWarning(
                        ex,
                        "Failed to resume Agent retry operation {OperationId}",
                        operation.OperationId);
                }
            }

            try
            {
                var cutoff = _timeProvider.GetUtcNow().UtcDateTime - RetentionWindow;
                await operations.DeleteFinishedBeforeAsync(cutoff, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up finished Agent retry operations");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A pass is an obligation, not a reason to terminate the Server.
            // The next scheduled pass will retry both recovery and cleanup.
            _logger.LogWarning(ex, "Failed to process Agent retry obligations");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DelayAsync(InitialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(stoppingToken);
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
