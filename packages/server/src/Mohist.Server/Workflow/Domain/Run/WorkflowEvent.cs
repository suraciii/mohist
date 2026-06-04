namespace Mohist.Server.Workflow.Domain.Run;

public abstract record WorkflowEvent;

public sealed record WorkflowRunStarted() : WorkflowEvent;
public sealed record WorkflowRunResumed() : WorkflowEvent;
public sealed record WorkflowRunPaused() : WorkflowEvent;
public sealed record WorkflowRunStopped() : WorkflowEvent;
public sealed record WorkflowRunCompleted() : WorkflowEvent;
public sealed record WorkflowRunFailed(string? Message) : WorkflowEvent;

public sealed record StageStarted(string Stage) : WorkflowEvent;
public sealed record StageCompleted(string Stage) : WorkflowEvent;
public sealed record StageFailed(string Stage, string? Reason) : WorkflowEvent;

public enum ApprovalResult { Approved, Rejected }

public sealed record StageApprovalRequested(string Stage) : WorkflowEvent;
public sealed record StageApprovalResolved(string Stage, ApprovalResult Result, string? Reason = null) : WorkflowEvent;

public sealed record TaskCompleted(string Stage, string TaskId) : WorkflowEvent;
public sealed record TaskFailed(string Stage, string TaskId, string? Message) : WorkflowEvent;

public sealed record CheckPassed(string Stage, string CheckName, string? Message) : WorkflowEvent;
public sealed record CheckFailed(string Stage, string CheckName, string? Message) : WorkflowEvent;
public sealed record CheckPending(string Stage, string CheckName, string? Message) : WorkflowEvent;
public sealed record RepairScheduled(string Stage, string CheckName, IReadOnlyList<string> TaskIds) : WorkflowEvent;
