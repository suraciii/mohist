using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowGrain : IGrainWithStringKey
{
    Task StartAsync(WorkflowStartInput? input = null);
    Task EnsureStartedAsync(WorkflowIssueContext context);
    Task RefreshIssueContextAsync(WorkflowIssueContext context);
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
    Task<WorkflowAssignmentResult> AssignWorkerAsync(string workerId);
    Task<WorkItem?> ClaimNextAsync(string workerId);
    Task<ReportAck> FailActiveWorkAsync(string workerId, string message);
    Task<ReportAck> ReceiveTaskReportAsync(string workerId, string workId, TaskReport report);
    Task<ReportAck> ReceiveCheckReportAsync(string workerId, string workId, CheckReport report);

    Task ReleaseStageLocksAsync(string stage, string reason);
    Task<string?> GetRunStatusAsync();
    Task<bool> IsStoppedOrTerminalAsync();
    Task<string?> GetAssignedWorkerIdAsync();
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
public sealed record WorkflowIssueContext(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] int IssueNumber,
    [property: Id(2)] int? EpicNumber);

[GenerateSerializer]
public sealed record RuntimeTaskInput(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Uses = null,
    [property: Id(3)] JsonElement? With = null,
    [property: Id(4)] string? Stage = null,
    [property: Id(5)] bool InvalidateChecks = false,
    [property: Id(6)] RecoveryDefinition? Recovery = null,
    [property: Id(7)] TaskArtifactCapture? Artifacts = null,
    [property: Id(8)] Dictionary<string, string>? SetVars = null,
    [property: Id(9)] int? RecoveryRemaining = null,
    [property: Id(10)] JsonElement? Expect = null);

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

/// <summary>
/// Report delivery ack. <see cref="Stale"/> is still a successful ack for
/// late, duplicate, or no-longer-current work; the worker retires awaitingAck
/// on both values.
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
/// without trusting worker-supplied identifiers.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowActiveWorkView(
    [property: Id(0)] string WorkId,
    [property: Id(1)] string WorkType,
    [property: Id(2)] string Stage,
    [property: Id(3)] string TaskRunId,
    [property: Id(4)] string? Title,
    [property: Id(5)] string? ProjectId = null,
    [property: Id(6)] int? IssueNumber = null);

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
