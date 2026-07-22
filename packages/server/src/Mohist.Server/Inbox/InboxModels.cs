namespace Mohist.Server.Inbox;

/// <summary>
/// Stable identifiers for the four MVP notification kinds. Stored as
/// the string value on <c>InboxItems.NotificationKind</c> (TEXT, max
/// 32 chars) and used as the rendering template key on the Web client.
/// </summary>
public static class NotificationKinds
{
    public const string WorkflowFailed = "workflow_failed";
    public const string ApprovalRequested = "approval_requested";
    public const string IssueStarted = "issue_started";
    public const string IssueCompleted = "issue_completed";

    public static bool IsDefined(string? value) => value is
        WorkflowFailed or
        ApprovalRequested or
        IssueStarted or
        IssueCompleted;
}

/// <summary>
/// Snapshot of an inbox item to be inserted by the projection layer.
/// Identity, project scope, and notification kind are required; the
/// store is responsible for generating <c>Id</c>, <c>CreatedAt</c>, and
/// for idempotency (re-inserting the same source plus
/// <c>SourceEventId</c> is a no-op that returns the existing row's id).
/// </summary>
public sealed record InboxItemDraft(
    string ProjectId,
    int IssueNumber,
    string IssueTitle,
    string NotificationKind,
    string SourceEventSource,
    string SourceEventId,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// Result of an idempotent insert. <see cref="AlreadyExisted"/> is true
/// when the projection has already produced an item for this source
/// event — the caller may log/skip but must not throw or duplicate the
/// row.
/// </summary>
public sealed record InboxInsertResult(string Id, bool AlreadyExisted);

/// <summary>
/// Read model of an inbox item returned by <see cref="InboxQuerier"/>.
/// Mirrors the <c>InboxItemRow</c> columns; the Web client renders the
/// product-facing text from <see cref="NotificationKind"/> and issue
/// identity — no pre-rendered <c>Text</c> is stored.
/// </summary>
public sealed class InboxItemView
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public int IssueNumber { get; init; }
    public string IssueTitle { get; init; } = "";
    public string NotificationKind { get; init; } = "";
    public string SourceEventSource { get; init; } = "";
    public string SourceEventId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }

    public bool IsRead => ReadAt.HasValue;
    public bool IsArchived => ArchivedAt.HasValue;
}

public sealed record InboxUnreadCount(int UnreadCount);

/// <summary>
/// Project-scoped inbox subscription preference state. Returned by
/// <see cref="InboxSubscriptionStore.GetAsync"/> — when no row exists,
/// all four kinds are synthesized as enabled. Toggles are keyed
/// by <see cref="NotificationKinds"/> value.
/// </summary>
public sealed record InboxSubscriptionState(
    bool WorkflowFailedEnabled = true,
    bool ApprovalRequestedEnabled = true,
    bool IssueStartedEnabled = true,
    bool IssueCompletedEnabled = true)
{
    public bool IsEnabled(string kind) => kind switch
    {
        NotificationKinds.WorkflowFailed => WorkflowFailedEnabled,
        NotificationKinds.ApprovalRequested => ApprovalRequestedEnabled,
        NotificationKinds.IssueStarted => IssueStartedEnabled,
        NotificationKinds.IssueCompleted => IssueCompletedEnabled,
        _ => false,
    };
}
