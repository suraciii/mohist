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
    Task ApproveAsync();
    Task RejectAsync(string? reason = null);
    Task RetryAsync();
    Task RerunAsync();
    Task<RuntimeTaskAddedResult> AddTaskAsync(RuntimeTaskInput task);
    Task<AddTasksBatchResult> AddTasksAsync(AddTasksBatchRequest request);
    Task<bool> HasIncompleteTaskUsingAsync(string uses);
    Task<bool> HasIncompleteTaskIdAsync(string id);
    Task<WorkDispatch?> GetWorkAsync(string runnerId);
    Task ReportResultAsync(string runnerId, string workId, WorkDispatchResult result);
    Task FailInFlightWorkAsync(string runnerId, string reason);
    Task<WorkflowVariablesSnapshot?> GetVariablesAsync();
    Task<WorkflowVariablesSnapshot> PatchVariablesAsync(string section, string patchJson);
    Task<WorkflowVariablesSnapshot> PatchStageVariablesAsync(string stage, string section, string patchJson);
    Task<WorkflowStatusSnapshot?> GetStatusAsync();
    Task<string?> GetDefinitionYamlAsync();
}

[GenerateSerializer]
public sealed record MetadataSnapshot(
    [property: Id(0)] string? Name = null,
    [property: Id(1)] Dictionary<string, string>? Labels = null,
    [property: Id(2)] Dictionary<string, string>? Annotations = null,
    [property: Id(3)] DateTimeOffset? CreatedAt = null)
{
    public static MetadataSnapshot? From(WorkflowRunMetadata? m) => m is null ? null
        : new MetadataSnapshot(m.Name, m.Labels, m.Annotations, m.CreatedAt);

    public WorkflowRunMetadata? ToDomain() => Name is null && Labels is null && Annotations is null && CreatedAt is null
        ? null : new WorkflowRunMetadata(Name, CreatedAt ?? DateTimeOffset.MinValue, Labels, Annotations);
}

[GenerateSerializer]
public sealed record WorkflowStartInput(
    [property: Id(0)] string? Variables = null,
    [property: Id(1)] Dictionary<string, Dictionary<string, string>>? StageVariables = null,
    [property: Id(2)] MetadataSnapshot? Metadata = null);

[GenerateSerializer]
public sealed record WorkflowVariablesSnapshot(
    [property: Id(0)] string Variables,
    [property: Id(1)] Dictionary<string, Dictionary<string, string>>? StageVariables = null);

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
public sealed record WorkflowStatusSnapshot(
    string WorkflowRunId,
    string Status,
    string? CurrentStage,
    List<StageStatusSnapshot> Stages,
    PendingWorkSnapshot? PendingWork,
    FailureStatusSnapshot? Failure,
    List<AvailableActionSnapshot> AvailableActions,
    MetadataSnapshot? Metadata = null);

[GenerateSerializer]
public sealed record StageStatusSnapshot(
    string Stage,
    string Status,
    int Order,
    List<TaskStatusSnapshot> Tasks,
    List<CheckStatusSnapshot> Checks,
    ApprovalStatusSnapshot? Approval,
    FailureStatusSnapshot? Failure);

[GenerateSerializer]
public sealed record FailureStatusSnapshot(
    string Reason,
    string? Stage,
    string? TaskId,
    string? CheckName,
    string? Message);

[GenerateSerializer]
public sealed record AvailableActionSnapshot(
    string Name,
    string Label,
    string? Target);

[GenerateSerializer]
public sealed record TaskStatusSnapshot(
    string Id,
    string Title,
    string? Uses,
    string Status);

[GenerateSerializer]
public sealed record CheckStatusSnapshot(
    string Name,
    string Title,
    string? Uses,
    string Status,
    string? Message);

[GenerateSerializer]
public sealed record PendingWorkSnapshot(
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title,
    string? Uses);

[GenerateSerializer]
public sealed record ApprovalStatusSnapshot(
    string Status,
    string? Output,
    string RequestedAt,
    string? RespondedAt);
