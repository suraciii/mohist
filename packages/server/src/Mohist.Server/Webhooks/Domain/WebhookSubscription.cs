namespace Mohist.Server.Webhooks.Domain;

public sealed class WebhookSubscription
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Advanced CEL filter applied in addition to the selected event types. Empty means no extra filter.</summary>
    public string Match { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string Status { get; set; } = WebhookSubscriptionStatus.Active;
    /// <summary>"all" delivers every event type; "selected" delivers only <see cref="EventTypes"/>.</summary>
    public string EventSelectionMode { get; set; } = WebhookEventSelectionMode.All;
    public IReadOnlyList<string> EventTypes { get; set; } = [];
    /// <summary>Endpoint authentication mode: "none", "bearer", "basic", "custom". Credentials live in the secret store.</summary>
    public string AuthType { get; set; } = WebhookAuthType.None;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class WebhookSubscriptionStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Archived = "archived";
}

public static class WebhookEventSelectionMode
{
    public const string All = "all";
    public const string Selected = "selected";
}

public static class WebhookAuthType
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string Basic = "basic";
    public const string Custom = "custom";
}

/// <summary>
/// Resolved endpoint-authentication material, ready to be applied as HTTP headers.
/// Values are never logged or returned by read APIs; this is the send-time view only.
/// </summary>
public sealed class WebhookAuthMaterial
{
    public string AuthType { get; init; } = WebhookAuthType.None;
    /// <summary>Header name -> header value, with secrets already placed. May be empty.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}
