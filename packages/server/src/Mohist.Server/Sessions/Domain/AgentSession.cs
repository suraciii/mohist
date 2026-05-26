namespace Mohist.Server.Sessions.Domain;

public sealed class AgentSession
{
    public string Id { get; }
    public AgentSessionIssueRef Issue { get; }
    public AgentSessionWorkRef Work { get; }
    public AgentSessionStatus Status { get; private set; }
    public string? Model { get; private set; }
    public DateTime CreatedAt { get; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? LastActivityAt { get; private set; }
    public string? FailureReason { get; private set; }
    public int? ExitCode { get; private set; }

    private AgentSession(
        string id,
        AgentSessionIssueRef issue,
        AgentSessionWorkRef work,
        AgentSessionStatus status,
        string? model,
        DateTime createdAt,
        DateTime? startedAt,
        DateTime? completedAt,
        DateTime? lastActivityAt,
        string? failureReason,
        int? exitCode)
    {
        Id = id;
        Issue = issue;
        Work = work;
        Status = status;
        Model = model;
        CreatedAt = createdAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        LastActivityAt = lastActivityAt;
        FailureReason = failureReason;
        ExitCode = exitCode;
    }

    public static AgentSession Create(string id, AgentSessionIssueRef issue, AgentSessionWorkRef work, DateTime now) =>
        new(id, issue, work, AgentSessionStatus.Created, null, now, null, null, null, null, null);

    public static AgentSession Restore(
        string id,
        AgentSessionIssueRef issue,
        AgentSessionWorkRef work,
        AgentSessionStatus status,
        string? model,
        DateTime createdAt,
        DateTime? startedAt,
        DateTime? completedAt,
        DateTime? lastActivityAt,
        string? failureReason,
        int? exitCode) =>
        new(id, issue, work, status, model, createdAt, startedAt, completedAt, lastActivityAt, failureReason, exitCode);

    public bool IsTerminal => Status is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

    public void Start(string? model, DateTime now)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Running;
        Model = model ?? Model;
        StartedAt ??= now;
        LastActivityAt = now;
    }

    public void RecordActivity(DateTime now)
    {
        if (IsTerminal) return;
        LastActivityAt = now;
    }

    public void MarkActive(string status, DateTime now, string? failureReason = null)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatusNames.ParseActive(status);
        LastActivityAt = now;
        FailureReason = failureReason ?? FailureReason;
    }

    public void Complete(DateTime now, int? exitCode)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Completed;
        CompletedAt = now;
        LastActivityAt = now;
        ExitCode = exitCode ?? ExitCode;
    }

    public void Fail(DateTime now, string? reason, int? exitCode = null)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Failed;
        CompletedAt = now;
        LastActivityAt = now;
        FailureReason = reason ?? FailureReason;
        ExitCode = exitCode ?? ExitCode;
    }

    public void Cancel(DateTime now, string? reason, int? exitCode = null)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Cancelled;
        CompletedAt = now;
        LastActivityAt = now;
        FailureReason = reason ?? FailureReason;
        ExitCode = exitCode ?? ExitCode;
    }
}

public sealed record AgentSessionIssueRef(string ProjectId, int IssueNumber);

public sealed record AgentSessionWorkRef(
    string WorkflowRunId,
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title);

public enum AgentSessionStatus
{
    Created,
    Running,
    Probing,
    Completed,
    Failed,
    Cancelled,
}

public static class AgentSessionStatusNames
{
    public static string ToName(AgentSessionStatus status) => status switch
    {
        AgentSessionStatus.Created => "created",
        AgentSessionStatus.Running => "running",
        AgentSessionStatus.Probing => "probing",
        AgentSessionStatus.Completed => "completed",
        AgentSessionStatus.Failed => "failed",
        AgentSessionStatus.Cancelled => "cancelled",
        _ => "created",
    };

    public static AgentSessionStatus Parse(string status) => status switch
    {
        "running" => AgentSessionStatus.Running,
        "probing" => AgentSessionStatus.Probing,
        "completed" => AgentSessionStatus.Completed,
        "failed" => AgentSessionStatus.Failed,
        "cancelled" => AgentSessionStatus.Cancelled,
        _ => AgentSessionStatus.Created,
    };

    public static AgentSessionStatus ParseActive(string status) => status == "probing"
        ? AgentSessionStatus.Probing
        : AgentSessionStatus.Running;
}
