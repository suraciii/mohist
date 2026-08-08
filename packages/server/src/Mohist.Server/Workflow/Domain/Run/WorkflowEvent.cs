using Mohist.Server.Workflow.Domain.Artifacts;

namespace Mohist.Server.Workflow.Domain.Run;

public union WorkflowEvent(
    WorkflowRunStarted,
    WorkflowRunResumed,
    WorkflowRunPaused,
    WorkflowRunStopped,
    WorkflowRunCompleted,
    WorkflowRunFailed,
    StageStarted,
    StageCompleted,
    StageFailed,
    StageApprovalRequested,
    StageApprovalResolved,
    FeedbackRequested,
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    CheckPassed,
    CheckFailed,
    CheckPending,
    WorkflowArtifactRecorded);

public sealed record WorkflowRunStarted;
public sealed record WorkflowRunResumed;
public sealed record WorkflowRunPaused;
public sealed record WorkflowRunStopped;
public sealed record WorkflowRunCompleted;
public sealed record WorkflowRunFailed(string? Message);

public sealed record StageStarted(string Stage);
public sealed record StageCompleted(string Stage);
public sealed record StageFailed(string Stage, string? Reason);

public enum ApprovalResult { Approved, Rejected }

public sealed record StageApprovalRequested(string Stage);
public sealed record StageApprovalResolved(
    string Stage,
    ApprovalResult Result,
    string? Reason = null,
    string? DecidedBy = null,
    string? DisplayName = null);

public sealed record FeedbackRequested(string Stage, string FeedbackId, string? Reason = null);

public sealed record TaskStarted(string Stage, string TaskId, string WorkerId);
public sealed record TaskCompleted(string Stage, string TaskId);
public sealed record TaskFailed(string Stage, string TaskId, string? Message);

public sealed record CheckPassed(string Stage, string CheckName, string? Message);
public sealed record CheckFailed(string Stage, string CheckName, string? Message);
public sealed record CheckPending(string Stage, string CheckName, string? Message);
