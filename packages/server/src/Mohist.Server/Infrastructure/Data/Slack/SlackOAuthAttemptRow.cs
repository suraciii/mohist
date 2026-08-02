namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackOAuthAttemptRow
{
    public string Id { get; set; } = string.Empty;
    public string ChildAppId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string StateHash { get; set; } = string.Empty;
    public string BotUserId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? BotTokenRef { get; set; }
    public string? FailureClass { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? SecretStoredAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}
