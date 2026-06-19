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
    // Legacy reject entry point. The grain implementation now routes
    // through the feedback loop. Kept for back-compat with any external
    // integration that still calls this method. Prefer RequestChangesAsync
    // for new code.
    Task RejectAsync(string? reason = null);
    Task<string> RequestChangesAsync(string body);
    Task RetryAsync();
    Task RerunAsync();
    Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task);
    Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request);
    Task<bool> HasIncompleteTaskWithUsesAsync(string uses);
    Task<bool> HasIncompleteTaskByIdAsync(string id);
    Task<WorkflowAssignmentResult> AssignRunnerAsync(string runnerId);
    Task<WorkflowStartMaterializationDispatch?> PrepareStartMaterializationAsync(string runnerId);
    Task RecordStartMaterializationFailureAsync(string runnerId, string? message);
    Task NotifyRunnerLostAsync(string runnerId);
    Task ReportResultAsync(string runnerId, string workId, WorkResult result);
    Task<string?> GetRunStatusAsync();
    Task<bool> IsStoppedOrTerminalAsync();
    Task<string?> GetClaimedRunnerIdAsync();
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
    [property: Id(3)] string? With = null,
    [property: Id(4)] string? Stage = null,
    [property: Id(5)] bool InvalidateChecks = false);

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
    [property: Id(3)] string? With = null);

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

[GenerateSerializer]
public sealed record WorkflowStartMaterializationDispatch(
    [property: Id(0)] WorkDispatch Dispatch);

public enum WorkflowAssignmentStatus
{
    Assigned,
    Rejected
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
    [property: Id(6)] string? IssueId = null);

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
