namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackThreadLaunchReservationRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string ThreadTs { get; set; } = string.Empty;
    public string LaunchMessageTs { get; set; } = string.Empty;
    public string SlackUserId { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
