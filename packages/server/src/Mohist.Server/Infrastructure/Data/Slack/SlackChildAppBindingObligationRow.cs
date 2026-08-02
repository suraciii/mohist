namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackChildAppBindingObligationRow
{
    public string Id { get; set; } = string.Empty;
    public string ChildAppId { get; set; } = string.Empty;
    public string AgentConnectionId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string ClaimToken { get; set; } = string.Empty;
    public string? FailureClass { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
