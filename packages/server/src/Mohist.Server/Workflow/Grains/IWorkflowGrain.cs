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
    Task RejectAsync(string? reason = null);
    Task RetryAsync();
    Task RerunAsync();
    Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task);
    Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request);
    Task<bool> HasIncompleteTaskWithUsesAsync(string uses);
    Task<bool> HasIncompleteTaskByIdAsync(string id);
    Task<WorkflowAssignmentResult> AssignRunnerAsync(string runnerId);
    Task ReportResultAsync(string runnerId, string workId, WorkResult result);
    Task<string?> GetRunStatusAsync();
    Task<string?> GetClaimedRunnerIdAsync();
    Task<string?> GetCurrentWorkIdAsync();
    Task DeactivateForTestAsync();
}

[GenerateSerializer]
public sealed record WorkflowStartInput(
    [property: Id(0)] string? Variables = null,
    [property: Id(1)] Dictionary<string, Dictionary<string, string>>? StageVariables = null,
    [property: Id(2)] string? Name = null,
    [property: Id(3)] Dictionary<string, string>? Labels = null,
    [property: Id(4)] Dictionary<string, string>? Annotations = null,
    [property: Id(5)] string? ProjectId = null,
    [property: Id(6)] string? IssueKey = null);

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

public enum WorkflowAssignmentStatus
{
    Assigned,
    Rejected
}
