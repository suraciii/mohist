namespace Mohist.Server.Infrastructure.Data.Agent;

public sealed class RoutingRuleRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public string Match { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string ResponsePrompt { get; set; } = string.Empty;
    public bool Continue { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
}
