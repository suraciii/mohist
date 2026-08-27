using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

/// <summary>
/// Completes connection-enable reprojection obligations that outlive the HTTP
/// request. The Active transition is durable first; this worker retries every
/// pending connection after a failure or process restart.
/// </summary>
public sealed class GitHubConnectionReprojectionWorker : BackgroundService
{
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubConnectionReprojectionWorker> _log;

    public GitHubConnectionReprojectionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<GitHubConnectionReprojectionWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var connections = scope.ServiceProvider.GetRequiredService<GitHubConnectionStore>();
            var synchronization = scope.ServiceProvider.GetRequiredService<GitHubIssueSynchronizationService>();
            var pending = await connections.ListPendingReprojectionsAsync(ct);
            var completed = 0;
            foreach (var connection in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (await synchronization.ReprojectConnectionAsync(connection, ct))
                    completed++;
            }
            return completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub connection reprojection pass failed");
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(stoppingToken);
            try
            {
                await Task.Delay(SafetyPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
