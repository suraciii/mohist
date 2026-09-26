using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Infrastructure.Data.Slack;

/// <summary>
/// One durable outbound delivery intent per row. The state machine
/// (<see cref="State"/>) is enforced by the store; the kind
/// (<see cref="Kind"/>) decides whether duplicate updates for the same
/// <see cref="DispatchRef"/> merge into the latest (ReplaceableProgress)
/// or stand alone (Terminal / ExplicitFailure / UserAction).
/// </summary>
public sealed class SlackOutboxRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string OwnerKind { get; set; } = SlackDeliveryOwnerKinds.Connection;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string? ThreadTs { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? DispatchRef { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? ClaimedByAdapterId { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    /// <summary>
    /// When the intent's outcome was first observed unknown. This is
    /// delivery history, not a retry timer: once set it survives a retry or
    /// a merge back to Pending, and only a confirmed provider identity
    /// clears it, so a later notice can never describe unknown content as
    /// never delivered. The uncertain-timeout sweep reads it only while the
    /// row is <c>DeliveryUncertain</c>.
    /// </summary>
    public DateTimeOffset? DeliveryUncertainAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
