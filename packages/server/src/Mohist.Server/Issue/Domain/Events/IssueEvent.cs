namespace Mohist.Server.Issue.Domain.Events;

public union IssueEvent(
    IssueCreated,
    IssueLabelsChanged,
    IssuePriorityChanged,
    IssueDraftChanged,
    IssuePrerequisiteAdded,
    IssuePrerequisiteRemoved,
    IssueWorkStarted,
    IssueWorkCompleted,
    IssueClosed,
    IssueArchived,
    IssueUnarchived,
    IssueReopened);

public sealed record IssueCreated(
    string Title,
    string Priority,
    string[] Labels,
    string? Risk,
    string? RepositoryRef);

public sealed record IssueLabelsChanged(
    string[] OldLabels,
    string[] NewLabels);

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

public sealed record IssueWorkStarted(
    string WorkflowRunId);

public sealed record IssueWorkCompleted(
    string WorkflowRunId);

public sealed record IssueClosed(
    string? Reason);

public sealed record IssueArchived;

public sealed record IssueUnarchived;

public sealed record IssueReopened;

