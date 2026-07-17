namespace Mohist.Server.Epic.Domain.Events;

public union EpicEvent(
    EpicCreated,
    EpicUpdated,
    EpicPriorityChanged,
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

public sealed record EpicStatusChanged(
    string OldStatus,
    string NewStatus);

public sealed record EpicClosed;

public sealed record EpicReopened;

/// <param name="IssueNumber">The issue number of the failed start target.</param>
/// <param name="Reason">A short reason string from the caught exception.</param>
public sealed record EpicStartAttemptFailed(
    int IssueNumber,
    string Reason);
