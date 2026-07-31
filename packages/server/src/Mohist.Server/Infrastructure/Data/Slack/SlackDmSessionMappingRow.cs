namespace Mohist.Server.Infrastructure.Data.Slack;

/// <summary>
/// One current-session mapping per Connection × DM conversation. The
/// unique index on <c>(ConnectionId, DmConversationId)</c> turns a
/// concurrent upsert from two redelivered messages into a deterministic
/// "first writer wins" outcome, matching the inbox / outbox store
/// contract. The mapping is read by the Slack DM ingress to decide
/// whether a message continues the existing AgentSession (follow-up) or
/// starts a new AgentJob+Session (first DM or explicit New task).
/// </summary>
public sealed class SlackDmSessionMappingRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string SlackUserId { get; set; } = string.Empty;
    public string DmConversationId { get; set; } = string.Empty;
    public string CurrentSessionId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}