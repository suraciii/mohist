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

    // Direct Agent (generic agent-launch) label keys.
    // Mirror the workflow-shaped columns above; the json_extract paths
    // reference GenericAgentSessionMetadata constants so the SQL and the
    // runtime metadata can never drift.
    public string? LabelAgentId { get; set; }
    public string? LabelAgentName { get; set; }
    public string? LabelAgentLaunchIssueNumber { get; set; }
    public string? LabelAgentLaunchEpicNumber { get; set; }
    public string? LabelAgentLaunchRepository { get; set; }
    public string? LabelAgentLaunchWorkspacePath { get; set; }

    public string? LabelTriggerEventId { get; set; }
    public string? LabelTriggerRuleId { get; set; }

    public string? LabelConnectionId { get; set; }
    public string? LabelSlackUserId { get; set; }
    public string? LabelSlackConversationId { get; set; }

    /// <summary>
    /// Stored projected copy of <c>State.status.activity</c>. Sourced from
    /// the persisted Session JSON so direct-session activity can be selected
    /// at the database boundary without deserializing the full state for
    /// every historical row on each <c>/api/agent/status</c> poll.
    /// Lowercased to match the existing <see cref="Status"/>
    /// convention; values are <c>"active"</c>, <c>"idle"</c>, or
    /// <c>"unknown"</c>.
    /// </summary>
    public string? Activity { get; set; }
}
