namespace Mohist.Server.Epic.Domain.Events;

public union EpicEvent(
    EpicCreated,
    EpicUpdated,
    EpicPriorityChanged,
    EpicIssueLinked,
    EpicIssueUnlinked,
    EpicStatusChanged,
    EpicClosed,
    EpicReopened,
    EpicStartAttemptFailed);

public sealed record EpicCreated(
    string Title,
    string? Description,
    string Priority);

public sealed record EpicUpdated(
    string? Title,
    string? Description,
    string? Priority);

public sealed record EpicPriorityChanged(
    string OldPriority,
    string NewPriority);

public sealed record EpicIssueLinked(
    string IssueId,
    int IssueNumber);

public sealed record EpicIssueUnlinked(
    string IssueId,
    int IssueNumber);

public sealed record EpicStatusChanged(
    string OldStatus,
    string NewStatus);

public sealed record EpicClosed;

public sealed record EpicReopened;

/// <param name="IssueId">The linked issue that failed to start.</param>
/// <param name="IssueNumber">The issue number of the failed start target.</param>
/// <param name="Reason">A short reason string from the caught exception.</param>
public sealed record EpicStartAttemptFailed(
    string IssueId,
    int IssueNumber,
    string Reason);