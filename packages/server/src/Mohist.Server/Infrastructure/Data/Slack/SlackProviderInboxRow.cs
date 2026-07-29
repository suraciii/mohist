namespace Mohist.Server.Infrastructure.Data.Slack;

/// <summary>
/// One accepted Slack message per row. The unique index on
/// <c>(ConnectionId, SlackMessageIdentity)</c> makes the store's
/// dedup-on-insert contract race-safe even when Slack or the adapter
/// re-deliver the same message identity concurrently.
/// </summary>
public sealed class SlackProviderInboxRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string SlackMessageIdentity { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string DmConversationId { get; set; } = string.Empty;
    public string SlackUserId { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
