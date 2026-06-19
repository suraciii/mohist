using Mohist.Server.Epic.Domain.Events;

namespace Mohist.Server.Epic.Domain;

public sealed partial class Epic
{
    public static Epic Create(
        string id,
        string projectId,
        int number,
        string title,
        string? description = null,
        string? priority = null,
        DateTime? now = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        var resolvedPriority = EpicPriority.From(priority);
        var resolvedDescription = description ?? "";
        var epic = new Epic
        {
            Id = id,
            ProjectId = projectId,
            Number = number,
            Title = title,
            Description = resolvedDescription,
            Priority = resolvedPriority.Value,
            Status = EpicStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        epic.RecordEvent(new EpicCreated(
            Title: title,
            Description: resolvedDescription,
            Priority: resolvedPriority.Value));
        return epic;
    }

    public void Update(string? title, string? description, string? priority, DateTime? now = null)
    {
        var changed = false;
        string? updatedTitle = null;
        string? updatedDescription = null;
        string? updatedPriority = null;
        if (title != null)
        {
            _title = RequireTitle(title);
            updatedTitle = _title;
            changed = true;
        }
        if (description != null)
        {
            _description = description ?? "";
            updatedDescription = _description;
            changed = true;
        }
        if (priority != null)
        {
            var oldPriority = _priority.Value;
            var newPriority = EpicPriority.From(priority).Value;
            if (oldPriority != newPriority)
            {
                _priority = EpicPriority.From(priority);
                updatedPriority = newPriority;
                RecordEvent(new EpicPriorityChanged(oldPriority, newPriority));
                changed = true;
            }
        }
        if (!changed) return;
        Touch(now);
        RecordEvent(new EpicUpdated(updatedTitle, updatedDescription, updatedPriority));
    }

    public void MarkDone(IReadOnlySet<int> undeliveredLinkedNumbers, DateTime? now = null)
    {
        EnsureNotTerminal(EpicStatus.Done);
        if (undeliveredLinkedNumbers.Count > 0)
            throw new EpicNotReadyToMarkDoneException(Id, undeliveredLinkedNumbers.Count);
        var oldStatus = _status;
        _status = EpicStatus.Done;
        Touch(now);
        RecordEvent(new EpicStatusChanged(ToStatusName(oldStatus), ToStatusName(_status)));
    }

    public void Close(DateTime? now = null)
    {
        EnsureNotTerminal(EpicStatus.Closed);
        var oldStatus = _status;
        _status = EpicStatus.Closed;
        Touch(now);
        RecordEvent(new EpicStatusChanged(ToStatusName(oldStatus), ToStatusName(_status)));
        RecordEvent(new EpicClosed());
    }

    public void LinkIssue(string issueId, int issueNumber, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(issueId))
            throw new ArgumentException("Issue id is required", nameof(issueId));
        if (_linkedIssueNumbers.ContainsKey(issueId)) return;
        if (_linkedIssueNumbers.ContainsValue(issueNumber))
            throw new EpicDuplicateLinkedIssueException(issueNumber);
        _linkedIssueNumbers[issueId] = issueNumber;
        Touch(now);
        RecordEvent(new EpicIssueLinked(issueId, issueNumber));
    }

    public void UnlinkIssue(string issueId, DateTime? now = null)
    {
        if (!_linkedIssueNumbers.TryGetValue(issueId, out var issueNumber)) return;
        _linkedIssueNumbers.Remove(issueId);
        Touch(now);
        RecordEvent(new EpicIssueUnlinked(issueId, issueNumber));
    }

    private void EnsureNotTerminal(EpicStatus requested)
    {
        if (_status is EpicStatus.Done or EpicStatus.Closed)
            throw new EpicAlreadyTerminalException(ToStatusName(_status), ToStatusName(requested));
    }

    private static string ToStatusName(EpicStatus status) => status switch
    {
        EpicStatus.Active => "active",
        EpicStatus.Done => "done",
        EpicStatus.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
