namespace Mohist.Server.Workflow.Sessions.Storage;

public class WorkflowSessionEventRecord
{
    public long Id { get; set; }
    public string WorkflowSessionId { get; set; } = string.Empty;
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? AcpSessionId { get; set; }
    public string? ProjectId { get; set; }
    public int? IssueNumber { get; set; }
    public string? WorkId { get; set; }
    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
