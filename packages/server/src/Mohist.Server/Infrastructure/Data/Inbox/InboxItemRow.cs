namespace Mohist.Server.Infrastructure.Data.Inbox;

public class InboxItemRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public string IssueTitle { get; set; } = string.Empty;
    public string NotificationKind { get; set; } = null!;
    public string SourceEventSource { get; set; } = null!;
    public string SourceEventId { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
