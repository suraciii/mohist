namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssueCommentRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public string? Author { get; set; }
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
