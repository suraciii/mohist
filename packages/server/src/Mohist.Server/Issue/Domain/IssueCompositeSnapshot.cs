namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Per-child state used by the parent aggregate's composite-advancement
/// transitions to decide and apply the parent's aggregated status. The grain
/// builds one snapshot per recompute by joining the parent-children index
/// with each child's read-model row.
/// </summary>
public sealed record ChildSnapshot(
    int Number,
    IssueStatus Status);

public sealed class IssueEmptyCompositeSnapshotException(int issueNumber)
    : InvalidOperationException($"Issue #{issueNumber} is not a parent; composite transitions require a non-empty children snapshot");