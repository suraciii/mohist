namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackOAuthStateRow
{
    public string Id { get; set; } = string.Empty;
    public string AgentAppId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string StateHash { get; set; } = string.Empty;
    public string? AuthorizationAttemptId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
