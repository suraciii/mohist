using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task HydrateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, string? model = null, Dictionary<string, string>? stageModels = null);
    Task<string> StartWorkflowAsync();
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
}

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
