using Mohist.Server.Contracts;
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
    TaskInterrupted,
    TaskCancelled,
    AgentTaskUpdateInterrupted,
    AgentTaskInterruptionLifecycleChanged,
    AgentTaskResultUnconfirmed,
    TaskBlocked,
    StageBlocked,
    WorkflowRunBlocked,
    CheckPassed,
    CheckFailed,
    CheckPending,
    ChecksInterrupted,
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
public sealed record TaskInterrupted(
    string Stage,
    string TaskId,
    string WorkId,
    string Reason,
    DateTimeOffset RecoveryDeadlineAt);
public sealed record TaskCancelled(string Stage, string TaskId);
public sealed record AgentTaskUpdateInterrupted(
    string Stage,
    string TaskId,
    string WorkId,
    string UpdateOperationId);

public sealed record AgentTaskInterruptionLifecycleChanged(
    string Stage,
    string TaskId,
    AgentWorkInterruptionTransition Transition);
public sealed record AgentTaskResultUnconfirmed(
    string Stage,
    string TaskId,
    string WorkId,
    string Reason,
    DateTimeOffset DeadlineAt);
/// <summary>
/// Blocked-attention events emitted exactly once by the durable release
/// boundary. <see cref="Reason"/> is the stable consumer category
/// (<c>agent-result-unconfirmed</c>); <see cref="ReasonCode"/> optionally
/// carries the settlement's original persisted reason so event consumers can
/// observe it without depending on another projection. It is additive: older
/// persisted events deserialize with a null reason code.
/// </summary>
public sealed record TaskBlocked(
    string Stage,
    string TaskId,
    string Reason,
    DateTimeOffset DeadlineAt,
    string? ReasonCode = null);

public sealed record StageBlocked(
    string Stage,
    string TaskId,
    string Reason,
    string? ReasonCode = null);

public sealed record WorkflowRunBlocked(
    string Stage,
    string TaskId,
    string Reason,
    DateTimeOffset DeadlineAt,
    string? ReasonCode = null);

public sealed record CheckPassed(string Stage, string CheckName, string? Message);
public sealed record CheckFailed(string Stage, string CheckName, string? Message);
public sealed record CheckPending(string Stage, string CheckName, string? Message);
public sealed record ChecksInterrupted(
    string Stage,
    string WorkId,
    string Reason,
    DateTimeOffset RecoveryDeadlineAt);
