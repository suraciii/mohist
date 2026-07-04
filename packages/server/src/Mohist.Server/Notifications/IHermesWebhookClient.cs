namespace Mohist.Server.Notifications;

public interface IHermesWebhookClient
{
    Task SendAsync(HermesIssueNotificationPayload payload, CancellationToken ct);
}
