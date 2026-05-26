namespace Mohist.Server.Sessions.Storage;

public class AgentSessionTranscriptEntry
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
