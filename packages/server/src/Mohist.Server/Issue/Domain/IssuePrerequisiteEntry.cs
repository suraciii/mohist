namespace Mohist.Server.Issue.Domain;

public class IssuePrerequisiteEntry
{
    public string ProjectId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public int PrerequisiteNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
