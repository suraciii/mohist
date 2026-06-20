namespace Mohist.Server.Infrastructure.Data.Sessions;

public class AgentSessionRow
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
    public string? RunnerId { get; set; }
    public string? AgentSessionId { get; set; }
    public string Status { get; set; } = "opened";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDataAt { get; set; }

    // Stored computed columns projected from State JSON via json_extract.
    // These replace the former AgentSessionLabels index table — SQLite
    // keeps them in sync automatically whenever State changes.
    public string? LabelProjectId { get; set; }
    public string? LabelSourceId { get; set; }
    public string? LabelSessionName { get; set; }
    public string? LabelIssueNumber { get; set; }
    public string? LabelWorkId { get; set; }
    public string? LabelWorkType { get; set; }
    public string? LabelStage { get; set; }
    public string? LabelSourceKind { get; set; }
}
