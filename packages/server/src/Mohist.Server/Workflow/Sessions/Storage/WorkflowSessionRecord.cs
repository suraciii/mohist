namespace Mohist.Server.Workflow.Sessions.Storage;

public class WorkflowSessionRecord
{
    public string Id { get; set; } = string.Empty;
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? AcpSessionId { get; set; }
    public string? ProjectId { get; set; }
    public int? IssueNumber { get; set; }
    public string? RunnerId { get; set; }
    public string Status { get; set; } = "created";
    public string? Model { get; set; }
    public string? WorkDir { get; set; }
    public int? ProcessPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? LastDataAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int? ExitCode { get; set; }
}
