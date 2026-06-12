namespace Mohist.Server.Infrastructure.Data.Sessions;

public class AgentSessionTranscriptPartRow
{
    public long Id { get; set; }
    public long TurnId { get; set; }
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
    public string Type { get; set; } = string.Empty;
    public string CorrelationKey { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public int RawEventCount { get; set; }
}
