namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssuePrerequisiteRow
{
    public string ProjectId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public int PrerequisiteNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
