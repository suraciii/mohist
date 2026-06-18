using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.Issue.Services.Attachments;

public sealed class AttachmentCleanupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AttachmentCleanupService> _log;

    public AttachmentCleanupService(IServiceScopeFactory scopeFactory, ILogger<AttachmentCleanupService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<AttachmentService>();
                var removed = await service.CleanupExpiredPendingAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                    _log.LogInformation("Removed {Count} expired pending attachments", removed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to clean up expired pending attachments");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
