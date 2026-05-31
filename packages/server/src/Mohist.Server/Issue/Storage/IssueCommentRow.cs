namespace Mohist.Server.Issue.Storage;

public class IssueCommentRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string IssueId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
