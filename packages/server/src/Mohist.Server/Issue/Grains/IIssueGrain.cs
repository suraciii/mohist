using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Domain;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task CreateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, RepositoryInfo? repository = null);
    Task<string> StartWorkAsync(WorkflowProjectContext? project = null);
    Task CompleteWorkAsync(string workflowRunId);
    Task CancelAsync();
    Task UpdateAsync(string title, string? body);
    Task UpdateFullAsync(UpdateIssueData data);
    Task ArchiveAsync();
    Task UnarchiveAsync();
    Task ReopenAsync();
    Task<IssueWorkflowStatus?> GetWorkflowStatusAsync();
    Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber);
    Task RemovePrerequisiteAsync(int prerequisiteNumber);
    Task<IssueStartEligibility> GetStartEligibilityAsync();
    Task<IssueCommentResult> AddCommentAsync(string body);
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
    WorkflowStatusView? Workflow);

[GenerateSerializer]
public sealed record WorkflowProjectContext(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Path,
    [property: Id(3)] string BaseBranch,
    [property: Id(4)] string? RepositoryName = null,
    [property: Id(5)] string? RepositoryRemote = null,
    [property: Id(6)] string? RepositoryPath = null,
    [property: Id(7)] string? RepositoryBaseBranch = null);

[GenerateSerializer]
public sealed record IssuePrerequisiteResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] string Code,
    [property: Id(2)] string Message)
{
    public static IssuePrerequisiteResult Added() => new(true, "ok", "Prerequisite added");
    public static IssuePrerequisiteResult IssueNotFound() => new(false, "issue_not_found", "Issue not found");
    public static IssuePrerequisiteResult PrerequisiteNotFound(int number) => new(false, "prerequisite_not_found", $"Issue #{number} not found");
    public static IssuePrerequisiteResult Circular() => new(false, "circular_prerequisite", "Issue cannot depend on itself");
}

[GenerateSerializer]
public sealed record IssueCommentResult(
    [property: Id(0)] string Id,
    [property: Id(1)] string Body);
