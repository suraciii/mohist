namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Snapshot the producer hands to <see cref="SlackOutboxStore.EnqueueAsync"/>.
/// <see cref="DispatchRef"/> ties multiple progress updates for the same
/// logical dispatch together; when a ReplaceableProgress row arrives for
/// an existing <c>(ConnectionId, DispatchRef)</c> Pending entry, the
/// store updates the existing row's payload rather than inserting a new
/// one. Non-replaceable kinds MUST NOT collide on an existing Pending
/// entry — terminal results stand alone so they are never silently
/// merged into a superseded progress update.
/// </summary>
public sealed record SlackOutboxDraft(
    string ProjectId,
    string ConnectionId,
    string WorkspaceTeamId,
    string ConversationId,
    string Kind,
    string? DispatchRef,
    string PayloadJson,
    string? ThreadTs = null);

/// <summary>
/// Result of <see cref="SlackOutboxStore.EnqueueAsync"/>. When
/// <see cref="MergedIntoExisting"/> is true the draft's payload
/// replaced the existing ReplaceableProgress row and no new row was
/// created — the caller may continue as if a fresh insert succeeded.
/// </summary>
public sealed record SlackOutboxEnqueueResult(string Id, bool MergedIntoExisting, bool Suppressed = false);

/// <summary>
/// Read model for the outbox. Mirrors the row columns plus the
/// per-state booleans that surface the lifecycle to operator tooling.
/// </summary>
public sealed class SlackOutboxEntry
{
    public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string? ThreadTs { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? DispatchRef { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public string? ClaimedByAdapterId { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? DeliveryUncertainAt { get; init; }
    public DateTimeOffset? DeadLetteredAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SlackOutboxList(IReadOnlyList<SlackOutboxEntry> Entries);
