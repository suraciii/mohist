namespace Mohist.Server.Epic.Domain.Events;

public union EpicEvent(
    EpicCreated,
    EpicUpdated,
    EpicPriorityChanged,
    EpicIssueLinked,
    EpicIssueUnlinked,
    EpicStatusChanged,
    EpicClosed);

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