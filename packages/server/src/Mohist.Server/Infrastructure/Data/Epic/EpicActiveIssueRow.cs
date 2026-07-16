namespace Mohist.Server.Infrastructure.Data.Epic;

public class EpicActiveIssueRow
{
    public string ProjectId { get; set; } = null!;
    public string IssueId { get; set; } = null!;
    public string EpicId { get; set; } = null!;
    public int EpicNumber { get; set; }
    public int IssueNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
