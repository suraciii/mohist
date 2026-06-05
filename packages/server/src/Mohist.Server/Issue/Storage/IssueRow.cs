namespace Mohist.Server.Issue.Storage;

public class IssueRow
{
    public string IssueId { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
    public string? ProjectId { get; set; }
    public int? Number { get; set; }
    public string? WorkflowRunId { get; set; }
}
