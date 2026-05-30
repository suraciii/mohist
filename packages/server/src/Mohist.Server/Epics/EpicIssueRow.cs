namespace Mohist.Server.Epics;

public class EpicIssueRow
{
    public string EpicId { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string IssueId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
