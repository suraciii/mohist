using Microsoft.Extensions.Logging;

namespace Mohist.Server.Notifications;

public sealed class BackgroundHermesIssueNotificationDispatcher : IHermesIssueNotificationDispatcher
{
    private readonly ILogger<BackgroundHermesIssueNotificationDispatcher> _log;

    public BackgroundHermesIssueNotificationDispatcher(ILogger<BackgroundHermesIssueNotificationDispatcher> log)
    {
        _log = log;
    }

    public void Dispatch(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _ = Task.Run(async () =>
        {
            try
            {
                await work(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled Hermes issue notification background task failure");
            }
        }, CancellationToken.None);
    }
}
