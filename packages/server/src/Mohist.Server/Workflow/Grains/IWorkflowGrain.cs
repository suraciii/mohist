using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowGrain : IGrainWithStringKey
{
    Task StartAsync(WorkflowStartInput? input = null);
    Task EnsureStartedAsync(WorkflowIssueContext context);
    Task EnsureStartedAsync(WorkflowIssueContext context, WorkflowStartSnapshot? snapshot);
    Task RefreshIssueContextAsync(WorkflowIssueContext context);
    Task ResumeAsync();
    Task PauseAsync(string? reason = null);
    Task StopAsync(string? reason = null);
    Task<WorkflowWithdrawalResult> WithdrawIfBeforeIntegrateAsync(string? reason = null);

    Task ApproveAsync(string? decidedBy = null, string? displayName = null);
    Task<string> RequestChangesAsync(string body, string? decidedBy = null, string? displayName = null);
    Task RetryAsync();
    Task RerunAsync();
    Task<WorkflowControlResult> RerunFromStageAsync(string stageId);
    Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task);
    Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request);
    Task<bool> HasIncompleteTaskWithUsesAsync(string uses);
    Task<bool> HasIncompleteTaskByIdAsync(string id);
    Task<WorkflowAssignmentResult> AssignWorkerAsync(string workerId);
    Task<WorkItem?> ClaimNextAsync(string workerId, string processGeneration);
    Task<WorkDispatch?> StoreActiveWorkDispatchAsync(string workerId, string workId, WorkDispatch dispatch);
    Task<WorkReportVerdict> FailActiveWorkAsync(string workerId, string workId, string processGeneration, string message);
    Task<WorkReportVerdict> InterruptActiveWorkAsync(string workerId, string reason);
    Task<WorkReportVerdict> AbandonActiveWorkAsync(string workerId, string workId, string reason);
    Task<WorkReportVerdict> RejectActiveWorkDispatchAsync(string workerId, string workId, ExecutionError error);
    Task<WorkReportVerdict> ReceiveTaskReportAsync(
        string workerId,
        string workId,
        TaskReport report);
    Task<WorkReportVerdict> ReceiveCheckReportAsync(string workerId, string workId, CheckReport report);
    Task<WorkReportVerdict> ReceiveAgentJobTerminalAsync(WorkflowAgentJobTerminalDelivery delivery) =>
        Task.FromResult(WorkReportVerdict.Refused);

    Task ReleaseStageLocksAsync(string stage, string reason);
    Task<string?> GetRunStatusAsync();
    Task<bool> IsStoppedOrTerminalAsync();
    Task<string?> GetAssignedWorkerIdAsync();
    Task<string?> GetCurrentWorkIdAsync();
    Task<WorkflowActiveWorkView?> GetActiveWorkAsync(string workId);
    Task<WorkflowFeedbackRecord?> GetFeedbackAsync(string feedbackId);
    Task<IReadOnlyList<WorkflowFeedbackRecord>> ListFeedbackAsync();
}

[GenerateSerializer]
public sealed record WorkflowAgentJobTerminalDelivery(
    [property: Id(0)] string DeliveryId,
    [property: Id(1)] string JobKey,
    [property: Id(2)] string InvocationId,
    [property: Id(3)] string CommandId,
    [property: Id(4)] string ActionAttemptId,
    [property: Id(5)] string WorkId,
    [property: Id(6)] string Stage,
    [property: Id(7)] string RequestFingerprint,
    [property: Id(8)] string Status,
    [property: Id(9)] string? Message,
    [property: Id(10)] string? Output,
    [property: Id(11)] string[]? ArtifactUploadIds,
    [property: Id(12)] string? FailureReason,
    [property: Id(13)] string? FailureCategory,
    [property: Id(14)] int? ExitCode,
    [property: Id(15)] string? ResultFingerprint,
    [property: Id(16)] string? AgentSessionId,
    [property: Id(17)] string? InitialInputId,
    [property: Id(18)] string? InitialTurnId,
    [property: Id(19)] List<RuntimeTaskInput>? AddTasks = null);

[GenerateSerializer]
public sealed record WorkflowStartInput(
    [property: Id(0)] string? Name = null,
    [property: Id(1)] Dictionary<string, string>? Labels = null,
    [property: Id(2)] Dictionary<string, string>? Annotations = null,
    [property: Id(3)] WorkflowRunMetadata? Metadata = null,
    [property: Id(4)] WorkspaceIdentity? Workspace = null);

[GenerateSerializer]
public sealed record WorkflowIssueContext(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] int IssueNumber,
    [property: Id(2)] int? EpicNumber,
    [property: Id(3)] string? WorkflowProfileId = null);

/// <summary>
/// immutable start snapshot carried by the
/// <c>IssueWorkStarted</c> durable event. Captured at the moment the
/// Issue transaction commits, it is replayed verbatim into the
/// WorkflowRun so dispatch/review/rebase read run-owned repository
/// facts rather than live Project metadata. Null on replay paths that
/// only refresh issue context (e.g. retry/rerun) where the run already
/// holds its snapshot.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowStartSnapshot(
    [property: Id(0)] WorkflowRepositoryContext? Repository,
    [property: Id(1)] WorkspaceIdentity? Workspace);

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
    [property: Id(3)] JsonElement? With = null,
    [property: Id(4)] JsonElement? Expect = null);

[GenerateSerializer]
public sealed record AddTasksBatchResult(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string Stage,
    [property: Id(2)] int AddedCount);

[GenerateSerializer]
public sealed record WorkflowAssignmentResult(
    [property: Id(0)] WorkflowAssignmentStatus Status,
    [property: Id(1)] string? OwnerWorkerId = null,
    [property: Id(2)] string? Reason = null);

public enum WorkflowAssignmentStatus
{
    Assigned,
    Rejected
}

public enum WorkflowWithdrawalDisposition
{
    Applied,
    Echo,
}

[GenerateSerializer]
public sealed record WorkflowWithdrawalResult(
    [property: Id(0)] WorkflowWithdrawalDisposition Disposition,
    [property: Id(1)] string? Reason = null)
{
    public bool IsApplied => Disposition == WorkflowWithdrawalDisposition.Applied;
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
/// context (<see cref="ActionAttemptId"/>) for a pending artifact upload
/// without trusting worker-supplied identifiers.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowActiveWorkView(
    [property: Id(0)] string WorkId,
    [property: Id(1)] string WorkType,
    [property: Id(2)] string Stage,
    [property: Id(3)] string ActionAttemptId,
    [property: Id(4)] string? Title,
    [property: Id(5)] string? ProjectId = null,
    [property: Id(6)] int? IssueNumber = null,
    [property: Id(7)] string? OwnerWorkflowRunId = null);

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

[GenerateSerializer]
public sealed record WorkflowFeedbackResolution(
    [property: Id(0)] string? ResolutionTaskId,
    [property: Id(1)] DateTimeOffset? ResolvedAt,
    [property: Id(2)] string? ResolutionSummary);
