namespace Mohist.Server.Sessions.Domain;

public sealed class WorkflowAgentSession
{
    public string Id { get; internal set; } = string.Empty;
    public string ProjectId { get; internal set; } = string.Empty;
    public int IssueNumber { get; internal set; }
    public string WorkflowRunId { get; internal set; } = string.Empty;
    public string SessionName { get; internal set; } = string.Empty;
    public string? WorkId { get; internal set; }
    public string? WorkType { get; internal set; }
    public string? Stage { get; internal set; }
    public string? Title { get; internal set; }
    public string? RunnerId { get; internal set; }
    public string? AgentSessionId { get; internal set; }
    public AgentSessionStatus Status { get; internal set; }
    public string? Model { get; internal set; }
    public string? WorkDir { get; internal set; }
    public string? ChangeDir { get; internal set; }
    public int? ProcessPid { get; internal set; }
    public DateTime CreatedAt { get; internal set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; internal set; }
    public DateTime? LastDataAt { get; internal set; }
    public DateTime? LastHeartbeatAt { get; internal set; }
    public DateTime? CompletedAt { get; internal set; }
    public string? FailureReason { get; internal set; }
    public int? ExitCode { get; internal set; }

    private WorkflowAgentSession()
    {
    }

    private WorkflowAgentSession(
        string id,
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string? workId,
        string? workType,
        string? stage,
        string? title,
        string? runnerId,
        string? agentSessionId,
        AgentSessionStatus status,
        string? model,
        string? workDir,
        string? changeDir,
        int? processPid,
        DateTime createdAt,
        DateTime? startedAt,
        DateTime? lastDataAt,
        DateTime? lastHeartbeatAt,
        DateTime? completedAt,
        string? failureReason,
        int? exitCode)
    {
        Id = id;
        ProjectId = projectId;
        IssueNumber = issueNumber;
        WorkflowRunId = workflowRunId;
        SessionName = sessionName;
        WorkId = workId;
        WorkType = workType;
        Stage = stage;
        Title = title;
        RunnerId = runnerId;
        AgentSessionId = agentSessionId;
        Status = status;
        Model = model;
        WorkDir = workDir;
        ChangeDir = changeDir;
        ProcessPid = processPid;
        CreatedAt = createdAt;
        StartedAt = startedAt;
        LastDataAt = lastDataAt;
        LastHeartbeatAt = lastHeartbeatAt;
        CompletedAt = completedAt;
        FailureReason = failureReason;
        ExitCode = exitCode;
    }

    public static WorkflowAgentSession Create(
        string id,
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string? runnerId,
        string? workId = null,
        string? workType = null,
        string? stage = null,
        string? title = null,
        DateTime? now = null) =>
        new(id, projectId, issueNumber, workflowRunId, sessionName,
            workId, workType, stage, title,
            runnerId, null,
            AgentSessionStatus.Created, null,
            null, null, null,
            now ?? DateTime.UtcNow, null, null, null, null, null, null);

    public static WorkflowAgentSession Restore(
        string id,
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string? workId,
        string? workType,
        string? stage,
        string? title,
        string? runnerId,
        string? agentSessionId,
        AgentSessionStatus status,
        string? model,
        string? workDir,
        string? changeDir,
        int? processPid,
        DateTime createdAt,
        DateTime? startedAt,
        DateTime? lastDataAt,
        DateTime? lastHeartbeatAt,
        DateTime? completedAt,
        string? failureReason,
        int? exitCode) =>
        new(id, projectId, issueNumber, workflowRunId, sessionName,
            workId, workType, stage, title,
            runnerId, agentSessionId,
            status, model,
            workDir, changeDir, processPid,
            createdAt, startedAt, lastDataAt, lastHeartbeatAt,
            completedAt, failureReason, exitCode);

    public bool IsTerminal => Status is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

    public void Start(string? model, DateTime now)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Running;
        Model = model ?? Model;
        StartedAt ??= now;
        LastDataAt = now;
        LastHeartbeatAt = now;
    }

    public void RecordActivity(DateTime now)
    {
        if (IsTerminal) return;
        LastDataAt = now;
        LastHeartbeatAt = now;
    }

    public void MarkActive(string status, DateTime now, string? failureReason = null)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatusNames.ParseActive(status);
        LastDataAt = now;
        LastHeartbeatAt = now;
        FailureReason = failureReason ?? FailureReason;
    }

    public void Complete(DateTime now, int? exitCode)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Completed;
        CompletedAt = now;
        LastDataAt = now;
        LastHeartbeatAt = now;
        ExitCode = exitCode ?? ExitCode;
    }

    public void Fail(DateTime now, string? reason, int? exitCode = null)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Failed;
        CompletedAt = now;
        LastDataAt = now;
        LastHeartbeatAt = now;
        FailureReason = reason ?? FailureReason;
        ExitCode = exitCode ?? ExitCode;
    }

    public void Cancel(DateTime now, string? reason, int? exitCode = null)
    {
        if (IsTerminal) return;
        Status = AgentSessionStatus.Cancelled;
        CompletedAt = now;
        LastDataAt = now;
        LastHeartbeatAt = now;
        FailureReason = reason ?? FailureReason;
        ExitCode = exitCode ?? ExitCode;
    }
}

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

    public static bool TryParse(string status, out AgentSessionStatus result)
    {
        result = Parse(status);
        return true;
    }
}
