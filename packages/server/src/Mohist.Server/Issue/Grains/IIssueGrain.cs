using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task HydrateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, string? model = null, Dictionary<string, string>? stageModels = null);
    Task<string> StartWorkflowAsync(WorkflowProjectContext? project = null);
    Task CloseAsync();
    Task<string?> GetWorkflowRunIdAsync();
    Task UpdateAsync(string title, string? body);
    Task UpdateFullAsync(UpdateIssueData data);
    Task ArchiveAsync();
    Task UnarchiveAsync();
    Task ReopenAsync();
    Task<IssueWorkflowStatus?> GetWorkflowStatusAsync();
    Task<IssueInfo> GetInfoAsync();
    Task SetStageAsync(string stage);
    Task SetRuntimeStatusAsync(string status, string? reason = null);
    Task SetApprovalStateAsync(ApprovalState? state);
    Task SetMergeStateAsync(string? state);
    Task ProjectWorkflowStateAsync(WorkflowIssueProjection projection);
}

[GenerateSerializer]
public sealed record WorkflowIssueProjection(
    [property: Id(0)] string Stage,
    [property: Id(1)] string RuntimeStatus,
    [property: Id(2)] string? BlockedReason,
    [property: Id(3)] ApprovalState? ApprovalState,
    [property: Id(4)] bool Completed);

[GenerateSerializer]
public sealed record IssueWorkflowStatus(
    string IssueId,
    int IssueNumber,
    string Title,
    string Stage,
    string RuntimeStatus,
    string? WorkflowRunId,
    string? ChangeDir,
    string? WorkspacePath,
    WorkflowStatusSnapshot? Workflow);

[GenerateSerializer]
public sealed record WorkflowProjectContext(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Path,
    [property: Id(3)] string BaseBranch);
