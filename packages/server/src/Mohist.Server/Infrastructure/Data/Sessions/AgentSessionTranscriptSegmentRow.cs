namespace Mohist.Server.Infrastructure.Data.Sessions;

public class AgentSessionTranscriptSegmentRow
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? AgentSessionId { get; set; }
    public string? WorkId { get; set; }
    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public long Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int RawEventCount { get; set; }
}
