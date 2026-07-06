namespace Mohist.Server.Agent.Domain;

public class AgentSubscription
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SubscriptionFilter Filter { get; set; } = new();
    public string ResponsePrompt { get; set; } = string.Empty;
    public int? Priority { get; set; }
    public string Status { get; set; } = SubscriptionStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed partial class SubscriptionFilter
{
    public string Type { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Subject { get; set; }
}

public static class SubscriptionStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
}
