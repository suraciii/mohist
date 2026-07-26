using Mohist.Server.Inbox;

namespace Mohist.Server.Notifications;

/// <summary>
/// Outbound Hermes webhook configuration. Bound from
/// <c>Mohist:Notifications:Hermes</c> in <c>~/.mohist/config.jsonc</c>.
/// </summary>
public sealed class HermesNotificationOptions
{
    public const string SectionName = "Mohist:Notifications:Hermes";

    public string? WebhookUrl { get; set; }

    public string? Secret { get; set; }

    public string[] EnabledTypes { get; set; } =
    [
        NotificationKinds.ApprovalRequested,
        NotificationKinds.WorkflowFailed,
        NotificationKinds.IssueCompleted,
        NotificationKinds.AgentResponseFailed,
    ];

    public bool IsWebhookConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);

    public bool IsEnabled(string notificationType) =>
        EnabledTypes.Any(t => string.Equals(t, notificationType, StringComparison.OrdinalIgnoreCase));
}
