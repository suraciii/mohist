namespace Mohist.Server.Issue.Domain.Events;

public union IssueEvent(
    IssueCreated,
    IssueLabelsChanged,
    IssuePriorityChanged,
    IssueDraftChanged,
    IssuePrerequisiteAdded,
    IssuePrerequisiteRemoved,
    IssueWorkflowProfileChanged,
    IssueEpicChanged,
    IssueWorkStarted,
    IssueCompleted,
    IssueCancelled,
    IssueArchived,
    IssueUnarchived,
    IssueReopened);

public sealed record IssueCreated(
    string Title,
    string Priority,
    IReadOnlyDictionary<string, string> Labels,
    string? Risk,
    string? RepositoryRef);

public sealed record IssueLabelsChanged(
    IReadOnlyDictionary<string, string> OldLabels,
    IReadOnlyDictionary<string, string> NewLabels);

public sealed record IssuePriorityChanged(
    string OldPriority,
    string NewPriority);

public sealed record IssueDraftChanged(
    bool OldIsDraft,
    bool NewIsDraft);

public sealed record IssuePrerequisiteAdded(
    int PrerequisiteNumber);

public sealed record IssuePrerequisiteRemoved(
    int PrerequisiteNumber);

public sealed record IssueWorkflowProfileChanged(
    string? WorkflowProfileId);

public sealed record IssueEpicChanged(
    int? PreviousEpicNumber,
    int? EpicNumber);

public sealed record IssueWorkStarted(
    string WorkflowRunId);

public sealed record IssueCompleted(
    string WorkflowRunId);

public sealed record IssueCancelled(
    string? Reason);

public sealed record IssueArchived;

public sealed record IssueUnarchived;

public sealed record IssueReopened;
