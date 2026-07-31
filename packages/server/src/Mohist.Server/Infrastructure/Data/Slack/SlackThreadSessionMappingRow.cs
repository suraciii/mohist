namespace Mohist.Server.Infrastructure.Data.Slack;

/// <summary>
/// One bound Slack channel-thread AgentSession per Connection. The
/// unique index on <c>(ConnectionId, WorkspaceTeamId, ConversationId,
/// ThreadTs)</c> keeps a single (workspace, channel, thread) row per
/// Connection — a Project can manage Connections in several workspaces
/// and the same Connection can be present in several channels, so
/// <c>WorkspaceTeamId</c> and <c>ConversationId</c> are part of the key
/// to prevent cross-workspace or cross-channel collisions on equal root
/// timestamps. Bind is append-once: a follow-up reply does not swap the
/// session (threads have no New-task semantics), and a second mention
/// of a different Agent lands in a separate row under the same thread.
/// </summary>
public sealed class SlackThreadSessionMappingRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string ThreadTs { get; set; } = string.Empty;
    public string SlackUserId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string RootMessageTs { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}