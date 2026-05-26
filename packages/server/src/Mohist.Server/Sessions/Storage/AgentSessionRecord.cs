using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Storage;

public class AgentSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string WorkType { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? Title { get; set; }
    public string RunnerId { get; set; } = string.Empty;
    public string? ExternalSessionId { get; set; }
    public string Status { get; set; } = "created";
    public string? Model { get; set; }
    public string? WorkDir { get; set; }
    public string? ChangeDir { get; set; }
    public int? ProcessPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastDataAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public string? FailureReason { get; set; }
    public int? ExitCode { get; set; }

    public AgentSession ToDomain() => AgentSession.Restore(
        Id,
        new AgentSessionIssueRef(ProjectId, IssueNumber),
        new AgentSessionWorkRef(WorkflowRunId, WorkId, WorkType, Stage, Title),
        AgentSessionStatusNames.Parse(Status),
        Model,
        CreatedAt,
        StartedAt,
        CompletedAt,
        LastDataAt,
        FailureReason,
        ExitCode);

    public void Apply(AgentSession session)
    {
        Status = AgentSessionStatusNames.ToName(session.Status);
        Model = session.Model;
        StartedAt = session.StartedAt;
        CompletedAt = session.CompletedAt;
        LastDataAt = session.LastActivityAt;
        LastHeartbeatAt = session.LastActivityAt;
        FailureReason = session.FailureReason;
        ExitCode = session.ExitCode;
    }
}
