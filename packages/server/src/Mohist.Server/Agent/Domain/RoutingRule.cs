namespace Mohist.Server.Agent.Domain;

public sealed class RoutingRule
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public string Match { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string ResponsePrompt { get; set; } = string.Empty;
    public bool Continue { get; set; }
    public string Status { get; set; } = RoutingRuleStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
}

public static class RoutingRuleStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
    public const string Deleted = "deleted";
}
