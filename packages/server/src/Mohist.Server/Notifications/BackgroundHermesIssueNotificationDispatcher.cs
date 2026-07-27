using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Notifications;

public sealed class BackgroundHermesIssueNotificationDispatcher : IHermesIssueNotificationDispatcher
{
    private readonly ILogger<BackgroundHermesIssueNotificationDispatcher> _log;
    private readonly IBackgroundTaskLauncher _backgroundTasks;

    public BackgroundHermesIssueNotificationDispatcher(
        ILogger<BackgroundHermesIssueNotificationDispatcher> log,
        IBackgroundTaskLauncher backgroundTasks)
    {
        _log = log;
        _backgroundTasks = backgroundTasks;
    }

    public void Dispatch(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _backgroundTasks.Launch(async _ =>
        {
            try
            {
                await work(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled Hermes issue notification background task failure");
            }
        });
    }
}
