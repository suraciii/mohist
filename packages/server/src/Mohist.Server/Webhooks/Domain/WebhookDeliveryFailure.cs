namespace Mohist.Server.Webhooks.Domain;

public sealed class WebhookDeliveryFailure
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    /// <summary>HTTP status code when the endpoint responded with non-2xx; null for transport/timeout errors.</summary>
    public int? ResponseStatus { get; set; }
    /// <summary>Request duration in milliseconds.</summary>
    public int? DurationMs { get; set; }
    public string ErrorSummary { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
