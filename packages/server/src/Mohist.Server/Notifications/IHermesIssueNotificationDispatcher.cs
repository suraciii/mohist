namespace Mohist.Server.Notifications;

public interface IHermesIssueNotificationDispatcher
{
    void Dispatch(Func<CancellationToken, Task> work);
}
