namespace Mohist.Server.Agent.Domain;

public sealed class AgentConnection
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string ProviderKind { get; set; } = ConnectionProviderKind.Slack;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string BotUserId { get; set; } = string.Empty;
    public string BotName { get; set; } = string.Empty;
    public string? AvatarHash { get; set; }
    public string SetupProgress { get; set; } = SetupProgressKind.CreateAppCredentials;
    public string DesiredState { get; set; } = DesiredStateKind.Enabled;
    public string ConnectionHealth { get; set; } = ConnectionHealthKind.Healthy;
    public string? HealthReason { get; set; }
    public string AgentReadiness { get; set; } = AgentReadinessKind.Unknown;
    public string? OwnerSlackUserId { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public static class ConnectionProviderKind
{
    public const string Slack = "slack";
}

public static class SetupProgressKind
{
    public const string CreateAppCredentials = "create_app_credentials";
    public const string WaitingForSlackService = "waiting_for_slack_service";
    public const string FixSlackSetup = "fix_slack_setup";
    public const string ClaimOwner = "claim_owner";
    public const string Complete = "complete";
}

public static class DesiredStateKind
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
}

public static class ConnectionHealthKind
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
}

public static class AgentReadinessKind
{
    public const string Unknown = "unknown";
    public const string NeedsSetup = "needs_setup";
    public const string Ready = "ready";
}