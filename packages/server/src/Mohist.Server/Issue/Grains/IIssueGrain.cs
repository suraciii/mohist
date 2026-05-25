using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task HydrateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, string? model = null, Dictionary<string, string>? stageModels = null, string? workflowProfileId = null);
    Task<string> StartWorkflowAsync(WorkflowProjectContext? project = null);
    Task CompleteWorkflowAsync(string workflowRunId);
    Task CloseAsync();
    Task<string?> GetWorkflowRunIdAsync();
    Task UpdateAsync(string title, string? body);
    Task UpdateFullAsync(UpdateIssueData data);
    Task ArchiveAsync();
    Task UnarchiveAsync();
    Task ReopenAsync();
    Task<IssueWorkflowStatus?> GetWorkflowStatusAsync();
    Task<IssueInfo> GetInfoAsync();
    Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber);
    Task RemovePrerequisiteAsync(int prerequisiteNumber);
    Task<IssueStartEligibility> GetStartEligibilityAsync();
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

[GenerateSerializer]
public sealed record WorkflowProjectContext(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Path,
    [property: Id(3)] string BaseBranch);

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
