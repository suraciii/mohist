namespace Mohist.Server.Infrastructure.Data.Epic;

public class EpicIssueRow
{
    public string EpicId { get; set; } = null!;
    public int EpicNumber { get; set; }
    public string ProjectId { get; set; } = null!;
    public string IssueId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
