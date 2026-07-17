namespace Mohist.Server.Inbox;

/// <summary>
/// Identity-only payload of the <c>com.mohist.inbox.item-persisted</c>
/// realtime hint. Carries the bare references the Web needs to invalidate
/// its cached inbox query (and decide whether to show a high-attention
/// notice) — never the full inbox state. The full state is always
/// re-fetched from the inbox HTTP API, which remains the source of truth.
/// </summary>
public sealed record InboxItemPersistedHint(
    string ItemId,
    string ProjectId,
    string Kind,
    int IssueNumber);
