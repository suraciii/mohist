namespace Mohist.Server.Sessions.Storage;

public class WorkflowAgentSessionRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? WorkId { get; set; }
    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public string? Title { get; set; }
    public string? RunnerId { get; set; }
    public string? AgentSessionId { get; set; }
    public string Status { get; set; } = "created";
    public string? Model { get; set; }
    public string? WorkDir { get; set; }
    public string? ChangeDir { get; set; }
    public int? ProcessPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? LastDataAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int? ExitCode { get; set; }
}
