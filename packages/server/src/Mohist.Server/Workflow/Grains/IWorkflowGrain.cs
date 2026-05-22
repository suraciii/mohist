using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowGrain : IGrainWithStringKey
{
    Task StartAsync(WorkflowDefinitionInput? definition = null);
    Task ResumeAsync();
    Task PauseAsync(string? reason = null);
    Task ApproveAsync();
    Task RejectAsync(string? reason = null);
    Task RetryAsync();
    Task RerunAsync();
    Task ReportResultAsync(string workId, WorkDispatchResult result);
}

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
