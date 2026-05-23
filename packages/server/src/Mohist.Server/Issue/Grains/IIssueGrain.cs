using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task<string> StartWorkflowAsync();
    Task CloseAsync();
    Task<string?> GetWorkflowRunIdAsync();
    Task UpdateAsync(string title, string? body);
    Task ArchiveAsync();
    Task<IssueWorkflowStatus?> GetWorkflowStatusAsync();
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
