using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowGrain : IGrainWithStringKey
{
    Task StartAsync(WorkflowStartInput? input = null);
    Task ResumeAsync();
    Task PauseAsync(string? reason = null);
    Task StopAsync(string? reason = null);

    Task ApproveAsync();
    Task<string> RequestChangesAsync(string body);
    Task RetryAsync();
    Task RerunAsync();
    Task<WorkflowControlResult> RerunFromStageAsync(string stageId);
    Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task);
    Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request);
    Task<bool> HasIncompleteTaskWithUsesAsync(string uses);
    Task<bool> HasIncompleteTaskByIdAsync(string id);
    Task<WorkflowAssignmentResult> AssignRunnerAsync(string runnerId);
    /// <summary>
    /// The single write that starts work. Picks the run's next pending work,
    /// acquires the sequential stage lock, marks the work Running with the
    /// runner identity, and persists — one atomic transition on the
    /// single-writer grain. Returns the claimed <see cref="WorkItem"/> (with its
    /// resolved work id), or <c>null</c> when there is no dispatchable work, the
    /// stage lock is contended, or the run is not Ready/Running/assigned to the
    /// caller. There is no offer phase: a claim that never reaches the runner
    /// needs no rollback — the work is Running and unreported, so the next poll
    /// re-dispatches it.
    /// </summary>
    Task<WorkItem?> ClaimNextAsync(string runnerId);
    Task<ReportAck> ReportTaskOutcomeAsync(string runnerId, string workId, TaskOutcome outcome);
    Task<ReportAck> ReportCheckOutcomeAsync(string runnerId, string workId, CheckOutcome outcome);

    /// <summary>
    /// Releases the sequential stage lock owned by this workflow run for a
    /// stage. Used by bus subscribers and by retry/rerun/stop cleanup paths.
    /// </summary>
    Task ReleaseStageLocksAsync(string stage, string reason);
    Task<string?> GetRunStatusAsync();
    Task<bool> IsStoppedOrTerminalAsync();
    Task<string?> GetAssignedRunnerIdAsync();
    Task<string?> GetCurrentWorkIdAsync();
    Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId);
    Task<WorkflowFeedbackRecord?> GetFeedbackAsync(string feedbackId);
    Task<IReadOnlyList<WorkflowFeedbackRecord>> ListFeedbackAsync();
    Task DeactivateForTestAsync();
}

[GenerateSerializer]
public sealed record WorkflowStartInput(
    [property: Id(0)] string? Name = null,
    [property: Id(1)] Dictionary<string, string>? Labels = null,
    [property: Id(2)] Dictionary<string, string>? Annotations = null,
    [property: Id(3)] WorkflowRunMetadata? Metadata = null,
    [property: Id(4)] WorkspaceIdentity? Workspace = null);

[GenerateSerializer]
public sealed record RuntimeTaskInput(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Uses = null,
    [property: Id(3)] JsonElement? With = null,
    [property: Id(4)] string? Stage = null,
    [property: Id(5)] bool InvalidateChecks = false,
    [property: Id(6)] RecoveryDefinition? Recovery = null);

[GenerateSerializer]
public sealed record RuntimeTaskAddedResult(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string Stage,
    [property: Id(2)] string TaskId);

[GenerateSerializer]
public sealed record AddTasksBatchRequest(
    [property: Id(0)] List<AddTasksBatchItem> Tasks);

[GenerateSerializer]
public sealed record AddTasksBatchItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Uses = null,
    [property: Id(3)] JsonElement? With = null);

[GenerateSerializer]
public sealed record AddTasksBatchResult(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string Stage,
    [property: Id(2)] int AddedCount);

[GenerateSerializer]
public sealed record WorkflowAssignmentResult(
    [property: Id(0)] WorkflowAssignmentStatus Status,
    [property: Id(1)] string? OwnerRunnerId = null,
    [property: Id(2)] string? Reason = null);

public enum WorkflowAssignmentStatus
{
    Assigned,
    Rejected
}

/// <summary>
/// The ack for an at-least-once report (<c>ReportTaskOutcomeAsync</c> /
/// <c>ReportCheckOutcomeAsync</c>). <see cref="Accepted"/> means the owner
/// consumed the outcome. <see cref="Stale"/> means the work was already
/// terminal, not assigned to the caller, or otherwise no longer current — the
/// result is discarded idempotently. Both are acks: the runner retires the work
/// from <c>awaitingAck</c> on either (see <c>design/workflow/scheduling.md</c>
/// §Report). A report for a work the owner does not recognize is Stale, never
/// an error — late/duplicate reports are the normal case under at-least-once.
/// </summary>
[GenerateSerializer]
public enum ReportAck
{
    Accepted,
    Stale
}

[GenerateSerializer]
public sealed record WorkflowControlResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] string? Code = null,
    [property: Id(2)] string? Error = null,
    [property: Id(3)] JsonElement? Details = null)
{
    public static WorkflowControlResult Ok() => new(true);

    public static WorkflowControlResult Rejected(string code, string error, JsonElement? details = null) =>
        new(false, code, error, details);
}

/// <summary>
/// Read-only snapshot of the active workflow work item. The upload
/// endpoint uses this to derive the server-side task run binding
/// context (<see cref="TaskRunId"/>) for a pending artifact upload
/// without trusting runner-supplied identifiers.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowActiveWorkView(
    [property: Id(0)] string WorkId,
    [property: Id(1)] string WorkType,
    [property: Id(2)] string Stage,
    [property: Id(3)] string TaskRunId,
    [property: Id(4)] string? Title,
    [property: Id(5)] string? ProjectId = null,
    [property: Id(6)] string? IssueId = null,
    [property: Id(7)] int? IssueNumber = null);

/// <summary>
/// Read-only snapshot of an approval feedback record for API
/// responses. Wraps the persisted <see cref="ApprovalFeedback"/>
/// with the workflow run id and issue number so callers can correlate
/// feedback with the workflow that owns it and the issue that
/// requested changes.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowFeedbackRecord(
    [property: Id(0)] string Id,
    [property: Id(1)] string WorkflowRunId,
    [property: Id(2)] string Stage,
    [property: Id(3)] string Body,
    [property: Id(4)] ApprovalFeedbackStatus Status,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] WorkflowFeedbackResolution? Resolution,
    [property: Id(7)] int? IssueNumber = null);

/// <summary>
/// Resolution sub-object for the stable, agent-readable feedback
/// JSON shape. <c>null</c> when the feedback is still open; populated
/// with the resolution task id, timestamp, and summary when the
/// apply-feedback task completes successfully.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowFeedbackResolution(
    [property: Id(0)] string? ResolutionTaskId,
    [property: Id(1)] DateTimeOffset? ResolvedAt,
    [property: Id(2)] string? ResolutionSummary);
