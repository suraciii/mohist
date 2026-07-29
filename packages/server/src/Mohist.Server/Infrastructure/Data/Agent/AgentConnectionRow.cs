namespace Mohist.Server.Infrastructure.Data.Agent;

public sealed class AgentConnectionRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string ProviderKind { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string BotUserId { get; set; } = string.Empty;
    public string BotName { get; set; } = string.Empty;
    public string? AvatarHash { get; set; }
    public string SetupProgress { get; set; } = string.Empty;
    public string DesiredState { get; set; } = string.Empty;
    public string ConnectionHealth { get; set; } = string.Empty;
    public string? HealthReason { get; set; }
    public string AgentReadiness { get; set; } = string.Empty;
    public string? OwnerSlackUserId { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}