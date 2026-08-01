namespace Mohist.Server.Infrastructure.Data.Webhooks;

public sealed class WebhookSubscriptionRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Match { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string EventSelectionMode { get; set; } = "all";
    /// <summary>JSON array of selected event types, e.g. <c>["com.mohist.issue.created"]</c>.</summary>
    public string EventTypes { get; set; } = "[]";
    public string AuthType { get; set; } = "none";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
