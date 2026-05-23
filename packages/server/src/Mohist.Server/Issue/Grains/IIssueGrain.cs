using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task HydrateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority);
    Task<string> StartWorkflowAsync();
    Task CloseAsync();
    Task<string?> GetWorkflowRunIdAsync();
    Task UpdateAsync(string title, string? body);
    Task ArchiveAsync();
    Task<IssueWorkflowStatus?> GetWorkflowStatusAsync();
    Task<IssueInfo> GetInfoAsync();
}

[GenerateSerializer]
public sealed record IssueWorkflowStatus(
    string IssueId,
    int IssueNumber,
    string Title,
    string IssueStatus,
    string? WorkflowRunId,
    string? ChangeDir,
    string? WorkspacePath,
    WorkflowStatusSnapshot? Workflow);
