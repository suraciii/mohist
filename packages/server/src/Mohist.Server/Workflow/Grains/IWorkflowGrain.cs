using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowGrain : IGrainWithStringKey
{
    Task StartAsync(WorkflowDefinition? definition = null, WorkflowStartInput? input = null);
    Task ResumeAsync();
    Task PauseAsync(string? reason = null);
    Task UnscheduleAsync(string reason);
    Task ApproveAsync();
    Task RejectAsync(string? reason = null);
    Task RetryAsync();
    Task RerunAsync();
    Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task);
    Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request);
    Task<bool> HasIncompleteTaskWithUsesAsync(string uses);
    Task<bool> HasIncompleteTaskByIdAsync(string id);
    Task<WorkDispatch?> GetWorkAsync(string runnerId);
    Task ReportResultAsync(string runnerId, string workId, WorkDispatchResult result);
    Task AbandonCurrentWorkAsync(string runnerId, string reason);
    Task PatchVariablesAsync(string section, string patchJson);
    Task PatchStageVariablesAsync(string stage, string section, string patchJson);
    Task UpdateProfileDefinitionAsync(WorkflowDefinition definition);
    Task<string?> GetRunStatusAsync();
    Task<string?> GetAssignedRunnerIdAsync();
    Task<string?> GetAssignedWorkIdAsync();
    Task DeactivateForTestAsync();
}

[GenerateSerializer]
public sealed record WorkflowStartInput(
    [property: Id(0)] string? Variables = null,
    [property: Id(1)] Dictionary<string, Dictionary<string, string>>? StageVariables = null,
    [property: Id(2)] string? Name = null,
    [property: Id(3)] Dictionary<string, string>? Labels = null,
    [property: Id(4)] Dictionary<string, string>? Annotations = null);

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
