namespace Mohist.Server.Infrastructure.Data.Webhooks;

public sealed class WebhookDeliveryFailureRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public int? ResponseStatus { get; set; }
    public int? DurationMs { get; set; }
    public string ErrorSummary { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
