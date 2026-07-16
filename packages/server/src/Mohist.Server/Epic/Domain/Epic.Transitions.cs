using Mohist.Server.Epic.Domain.Events;

namespace Mohist.Server.Epic.Domain;

public sealed partial class Epic
{
    public static Epic Create(
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
            throw new EpicStartRequiresIdleException(Number, EpicStatusName.ToName(_status));
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
            throw new EpicPauseRequiresRunningException(Number, EpicStatusName.ToName(_status));
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
            throw new EpicResumeRequiresPausedException(Number, EpicStatusName.ToName(_status));
        var oldStatus = _status;
        _status = EpicStatus.Running;
        _pauseReason = null;
        Touch(now);
        RecordEvent(new EpicStatusChanged(EpicStatusName.ToName(oldStatus), EpicStatusName.ToName(_status)));
    }

    public void MarkDone(IReadOnlySet<int> openLinkedNumbers, DateTime? now = null)
    {
        if (_status is EpicStatus.Paused)
            throw new EpicPausedCannotMarkDoneException(Number);
        EnsureNotTerminal(EpicStatus.Done);
        if (openLinkedNumbers.Count > 0)
            throw new EpicNotReadyToMarkDoneException(Number, openLinkedNumbers.Count);
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
            throw new EpicNotTerminalException(Number, EpicStatusName.ToName(_status));
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

    public void LinkIssue(int issueNumber, DateTime? now = null)
    {
        if (issueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(issueNumber));
        if (_status is EpicStatus.Closed)
            throw new EpicClosedCannotLinkException(Number);
        if (_linkedIssueNumbers.Contains(issueNumber))
            throw new EpicDuplicateLinkedIssueException(issueNumber);
        _linkedIssueNumbers.Add(issueNumber);
        Touch(now);
        RecordEvent(new EpicIssueLinked(issueNumber));
    }

    public void UnlinkIssue(int issueNumber, DateTime? now = null)
    {
        if (!_linkedIssueNumbers.Remove(issueNumber)) return;
        Touch(now);
        RecordEvent(new EpicIssueUnlinked(issueNumber));
    }

    public void RecordStartAttemptFailure(int issueNumber, string reason, DateTime? now = null)
    {
        Touch(now);
        RecordEvent(new EpicStartAttemptFailed(issueNumber, reason));
    }

    private void EnsureNotTerminal(EpicStatus requested)
    {
        if (_status is EpicStatus.Done or EpicStatus.Closed)
            throw new EpicAlreadyTerminalException(EpicStatusName.ToName(_status), EpicStatusName.ToName(requested));
    }
}
