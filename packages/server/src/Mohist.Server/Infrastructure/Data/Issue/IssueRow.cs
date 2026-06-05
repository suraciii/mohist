namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssueRow
{
    public string IssueId { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
    public string? ProjectId { get; set; }
    public int? Number { get; set; }
    public string? WorkflowRunId { get; set; }
}
