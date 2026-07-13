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
            Status = EpicStatus.Idle,
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

    public void Start(DateTime? now = null)
    {
        EnsureNotTerminal(EpicStatus.Running);
        if (_status is EpicStatus.Running) return;
        if (_status is not EpicStatus.Idle)
            throw new EpicStartRequiresIdleException(Id, EpicStatusName.ToName(_status));
        var oldStatus = _status;
        _status = EpicStatus.Running;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
    }

    public void Pause(string? reason, DateTime? now = null)
    {
        EnsureNotTerminal(EpicStatus.Paused);
        if (_status is EpicStatus.Paused) return;
        if (_status is not EpicStatus.Running)
            throw new EpicPauseRequiresRunningException(Id, EpicStatusName.ToName(_status));
        var oldStatus = _status;
        _status = EpicStatus.Paused;
        _pauseReason = reason;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
    }

    public void Resume(DateTime? now = null)
    {
        EnsureNotTerminal(EpicStatus.Running);
        if (_status is EpicStatus.Running) return;
        if (_status is not EpicStatus.Paused)
            throw new EpicResumeRequiresPausedException(Id, EpicStatusName.ToName(_status));
        var oldStatus = _status;
        _status = EpicStatus.Running;
        _pauseReason = null;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
    }

    public void MarkDone(IReadOnlySet<int> openLinkedNumbers, DateTime? now = null)
    {
        if (_status is EpicStatus.Paused)
            throw new EpicPausedCannotMarkDoneException(Id);
        EnsureNotTerminal(EpicStatus.Done);
        if (openLinkedNumbers.Count > 0)
            throw new EpicNotReadyToMarkDoneException(Id, openLinkedNumbers.Count);
        var oldStatus = _status;
        _status = EpicStatus.Done;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
    }

    public void Close(DateTime? now = null)
    {
        EnsureNotTerminal(EpicStatus.Closed);
        var oldStatus = _status;
        _status = EpicStatus.Closed;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
        RecordEvent(new EpicClosed());
    }

    public void Reopen(DateTime? now = null)
    {
        if (_status is not (EpicStatus.Done or EpicStatus.Closed))
            throw new EpicNotTerminalException(Id, EpicStatusName.ToName(_status));
        var oldStatus = _status;
        _status = EpicStatus.Idle;
        _pauseReason = null;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
        RecordEvent(new EpicReopened());
    }

    public void WakeFromDone(DateTime? now = null)
    {
        if (_status is not EpicStatus.Done)
            throw new EpicAlreadyTerminalException(EpicStatusName.ToName(_status), EpicStatusName.Running);
        var oldStatus = _status;
        _status = EpicStatus.Running;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
    }

    public void LinkIssue(string issueId, int issueNumber, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(issueId))
            throw new ArgumentException("Issue id is required", nameof(issueId));
        if (_status is EpicStatus.Closed)
            throw new EpicClosedCannotLinkException(Id);
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

    public void RecordStartAttemptFailure(string issueId, int issueNumber, string reason, DateTime? now = null)
    {
        Touch(now);
        RecordEvent(new EpicStartAttemptFailed(issueId, issueNumber, reason));
    }

    private void EnsureNotTerminal(EpicStatus requested)
    {
        if (_status is EpicStatus.Done or EpicStatus.Closed)
            throw new EpicAlreadyTerminalException(EpicStatusName.ToName(_status), EpicStatusName.ToName(requested));
    }
}
