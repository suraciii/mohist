namespace Mohist.Server.Infrastructure.Data.Sessions;

public class AgentSessionTranscriptTurnRow
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? AgentSessionId { get; set; }
    public long Sequence { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string PromptKind { get; set; } = "task";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
