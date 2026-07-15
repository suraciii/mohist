using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null);
    Task<string> StartWorkAsync(WorkflowProjectContext? project = null);
    Task EnsureWorkflowBindingAsync(string workflowRunId);
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
    Task<IssueStartReadiness> GetStartReadinessAsync();
    Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null);
    Task DeactivateForTestAsync();

    /// <summary>
    /// Apply the affiliation resolved by durable Epic coordination. Issue
    /// persists its own producer snapshot and propagates its revision to the
    /// current WorkflowRun in a separate aggregate command.
    /// </summary>
    Task SetEpicAffiliationAsync(string? epicId);
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
    [property: Id(2)] string? RepositoryName = null,
    [property: Id(3)] string? RepositoryGitUrl = null,
    [property: Id(4)] string? RepositoryBaseBranch = null);

[GenerateSerializer]
public sealed record IssuePrerequisiteResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] string Code,
    [property: Id(2)] string Message)
{
    public static IssuePrerequisiteResult Added() => new(true, "ok", "Prerequisite added");
    public static IssuePrerequisiteResult IssueNotFound() => new(false, "issue_not_found", "Issue not found");
    public static IssuePrerequisiteResult PrerequisiteNotFound(int number) => new(false, "prerequisite_not_found", $"Issue #{number} not found");
    public static IssuePrerequisiteResult Circular(string? message = null) => new(false, "circular_prerequisite", message ?? "Issue cannot depend on itself");
}

[GenerateSerializer]
public sealed record IssueCommentResult(
    [property: Id(0)] string Id,
    [property: Id(1)] string Body);
