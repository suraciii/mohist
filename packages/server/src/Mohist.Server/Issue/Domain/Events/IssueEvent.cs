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
    IssueParentChanged,
    IssueWorkStarted,
    IssueCompleted,
    IssueCancelled,
    IssueArchived,
    IssueUnarchived,
    IssueReopened,
    IssueRepositoryChanged,
    IssueCompositeStarted,
    IssueCompositeStatusChanged);

public sealed record IssueCreated(
    string Title,
    string Priority,
    IReadOnlyDictionary<string, string> Labels,
    string? Risk,
    string? RepositoryRef);

public sealed record IssueRepositoryChanged(
    string? OldRepositoryRef,
    string NewRepositoryRef,
    string CommandId,
    long? ExpectedRevision,
    long? AppliedRevision);

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

public sealed record IssueParentChanged(
    int? PreviousParentIssueNumber,
    int? ParentIssueNumber);

public sealed record IssueWorkStarted(
    string WorkflowRunId,
    IssueWorkStartedRepository? Repository = null,
    IssueWorkStartedWorkspace? Workspace = null,
    IssueWorkStartedContext? Context = null);

public sealed record IssueWorkStartedRepository(
    string Name,
    string GitUrl,
    string BaseBranch);

public sealed record IssueWorkStartedWorkspace(
    string Path,
    string? Branch,
    string? ChangeDir);

public sealed record IssueWorkStartedContext(
    string ProjectId,
    int IssueNumber,
    string? Title,
    string? Priority);

public sealed record IssueCompleted(
    string WorkflowRunId);

public sealed record IssueCancelled(
    string? Reason);

public sealed record IssueArchived;

public sealed record IssueUnarchived;

public sealed record IssueReopened;

/// <summary>
/// Emitted when a parent issue transitions from Backlog to InProgress via
/// composite advancement (start of its children). The parent never owns a
/// workflow run of its own; this event marks the parent's aggregated
/// in-progress state without a <c>WorkflowRunId</c>.
/// </summary>
public sealed record IssueCompositeStarted;

/// <summary>
/// Emitted when a parent issue's aggregated status changes due to a child
/// state transition (terminal reached, child reopened, all-children-detached
/// recompute, etc.). Covers the four legal uses: InProgress→Done,
/// InProgress→Cancelled, Done→InProgress (child reopen), and
/// Backlog→Cancelled (direct cancel).
/// </summary>
public sealed record IssueCompositeStatusChanged(
    string PreviousStatus,
    string NewStatus);
