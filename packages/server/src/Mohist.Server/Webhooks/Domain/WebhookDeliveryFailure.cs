namespace Mohist.Server.Webhooks.Domain;

public sealed class WebhookDeliveryFailure
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string ErrorSummary { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
