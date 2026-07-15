namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssueRow
{
    public string IssueId { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
    public string? ProjectId { get; set; }
    public int? Number { get; set; }
    public string? Status { get; set; }
    public string? WorkflowRunId { get; set; }
    public bool? IsArchived { get; set; }
    public string? Title { get; set; }
    public string? Priority { get; set; }
    public bool? IsDraft { get; set; }
    public string? PrerequisiteNumbersJson { get; set; }
    public string? Risk { get; set; }
    public string? EpicId { get; set; }
}
