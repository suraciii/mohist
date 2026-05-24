namespace Mohist.Server.Events;

public class WorkflowEventEntry
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string? IssueId { get; set; }
    public int IssueNumber { get; set; }
    public string? WorkflowRunId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? TaskId { get; set; }
    public string? CheckName { get; set; }
    public string? RunnerId { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
