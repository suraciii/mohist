namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Snapshot the adapter hands to the inbox store on accept. The
/// <see cref="Identity"/> drives dedup; <see cref="SlackUserId"/> and
/// <see cref="ConversationId"/> are denormalized for read-side query
/// without rejoining against AgentConnections.
/// </summary>
public sealed record SlackProviderInboxDraft(
    string ProjectId,
    string ConnectionId,
    SlackMessageIdentity Identity,
    string SlackUserId,
    string? ThreadTs = null);

/// <summary>
/// Result of <see cref="SlackProviderInboxStore.AcceptAsync"/>. When
/// <see cref="AlreadyExisted"/> is true the existing row id is returned
/// and no new write happened — the redelivery is treated as "already
/// accepted" and the caller MUST NOT create a second SessionInput for it.
/// </summary>
public sealed record SlackProviderInboxAcceptResult(string Id, bool AlreadyExisted);

public static class SlackProviderInboxRouteKinds
{
    public const string Launch = "launch";
    public const string NewTaskLaunch = "new_task_launch";
    public const string Followup = "followup";
    public const string Cancel = "cancel";
    public const string Stop = "stop";
    public const string NoActiveWork = "no_active_work";
    public const string AlreadyEnded = "already_ended";
    public const string LaunchThread = "launch_thread";
    public const string FollowupThread = "followup_thread";
    public const string Ambiguous = "ambiguous";
    public const string Ignored = "ignored";
}

public sealed record SlackProviderInboxRouteDraft(
    string Kind,
    string? SessionId = null,
    string? TurnId = null);

public sealed record SlackProviderInboxRoute(
    string Kind,
    string? SessionId,
    string? TurnId);

/// <summary>
/// Read model of an inbox row, returned by <c>ListAsync</c> for operator
/// inspection. <see cref="IsPending"/> mirrors the capacity count: a row
/// whose <see cref="DispatchedAt"/> is set has been handed to the
/// launcher and no longer contributes to the per-connection cap.
/// </summary>
public sealed class SlackProviderInboxEntry
{
    public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string SlackMessageIdentity { get; init; } = string.Empty;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string? ThreadTs { get; init; }
    public string SlackUserId { get; init; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? DispatchedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsPending => DispatchedAt is null;
}

public sealed record SlackProviderInboxList(IReadOnlyList<SlackProviderInboxEntry> Entries);
