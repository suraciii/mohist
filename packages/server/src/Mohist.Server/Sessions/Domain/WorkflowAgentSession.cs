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
}
