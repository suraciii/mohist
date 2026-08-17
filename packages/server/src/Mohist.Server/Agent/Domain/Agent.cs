using System.Text.Json;

namespace Mohist.Server.Agent.Domain;

public class Agent
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string? Purpose { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public JsonElement? AgentConfig { get; set; }
    public IReadOnlyList<string> Skills { get; set; } = [];
    public IReadOnlyList<string> Permissions { get; set; } = [];
    public IReadOnlyList<string> AllowedSubagentAgentIds { get; set; } = [];
    public int? MaxConcurrentRuns { get; set; }
    public string Status { get; set; } = AgentStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Set only for task-first definitions. These facts survive a crash
    // between definition creation and coordinator-plan persistence.
    public string? TaskFirstIdempotencyKey { get; set; }
    public string? TaskFirstRequestFingerprint { get; set; }
}

public static class AgentStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
}
