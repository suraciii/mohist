namespace Mohist.Server.Infrastructure.Data.Sessions;

public class AgentSessionTranscriptTurnRow
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? RuntimeSessionId { get; set; }
    public long Sequence { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string PromptKind { get; set; } = "task";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
