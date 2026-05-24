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
    Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber);
    Task RemovePrerequisiteAsync(int prerequisiteNumber);
    Task<IssueStartEligibility> GetStartEligibilityAsync();
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

[GenerateSerializer]
public sealed record WorkflowIssueContext(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string IssueId,
    [property: Id(2)] int IssueNumber,
    [property: Id(3)] string ProjectName,
    [property: Id(4)] string ProjectPath,
    [property: Id(5)] string BaseBranch);

[GenerateSerializer]
public sealed record WorkflowIssueSeed(
    [property: Id(0)] string Title,
    [property: Id(1)] string Body,
    [property: Id(2)] string? Model,
    [property: Id(3)] Dictionary<string, string>? StageModels);

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
