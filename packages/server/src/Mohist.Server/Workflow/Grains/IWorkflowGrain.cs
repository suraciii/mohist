using Mohist.Server.Runner.Grains;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowGrain : IGrainWithStringKey
{
    Task StartAsync(WorkflowDefinitionInput? definition = null, WorkflowIssueContext? issue = null, WorkflowStartInput? input = null);
    Task ResumeAsync();
    Task PauseAsync(string? reason = null);
    Task ApproveAsync();
    Task RejectAsync(string? reason = null);
    Task RetryAsync();
    Task RerunAsync();
    Task<WorkDispatch?> GetWorkAsync(string runnerId);
    Task ReportResultAsync(string runnerId, string workId, WorkDispatchResult result);
    Task FailInFlightWorkAsync(string runnerId, string reason);
    Task<WorkflowStatusSnapshot?> GetStatusAsync();
}

[GenerateSerializer]
public sealed record WorkflowStartInput(
    [property: Id(0)] string? Variables = null);

[GenerateSerializer]
public sealed record WorkflowDefinitionInput(List<StageDefinitionInput> Stages);

[GenerateSerializer]
public sealed record StageDefinitionInput(
    string Stage,
    List<TaskDefinitionInput> Tasks,
    List<CheckDefinitionInput> Checks,
    string? TasksFromUses = null,
    string? TasksFromWith = null,
    bool RequiresApproval = false);

[GenerateSerializer]
public sealed record TaskDefinitionInput(
    string Id,
    string Title,
    string? Uses = null,
    string? With = null);

[GenerateSerializer]
public sealed record CheckDefinitionInput(
    string Name,
    string Title,
    string? Uses = null,
    string? With = null,
    int RetryLimit = 0,
    TaskDefinitionInput? RetryTask = null);

[GenerateSerializer]
public sealed record WorkflowStatusSnapshot(
    string WorkflowRunId,
    string Status,
    string? CurrentStage,
    string? ChangeDir,
    List<StageStatusSnapshot> Stages,
    PendingWorkSnapshot? PendingWork,
    FailureStatusSnapshot? Failure,
    List<AvailableActionSnapshot> AvailableActions);

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
