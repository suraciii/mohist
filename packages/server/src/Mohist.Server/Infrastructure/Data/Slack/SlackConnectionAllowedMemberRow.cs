namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackConnectionAllowedMemberRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string SlackUserId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
