namespace Mohist.Server.Infrastructure.Data.Sessions;

public class AgentSessionRow
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? WorkId { get; set; }
    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public string? RunnerId { get; set; }
    public string? AgentSessionId { get; set; }
    public string Status { get; set; } = "opened";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDataAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
