namespace Mohist.Server.Webhooks.Domain;

public sealed class WebhookSubscription
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Match { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string Status { get; set; } = WebhookSubscriptionStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class WebhookSubscriptionStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Archived = "archived";
}
