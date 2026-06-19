using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Issue.Domain;

public sealed partial class Issue
{
    public static Issue Create(
        string id,
        string projectId,
        int number,
        string title,
        string? body = null,
        IReadOnlyDictionary<string, string>? labels = null,
        string priority = "p2",
        string? repositoryRef = null,
        string? risk = null,
        bool isDraft = true,
        DateTime? now = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        var issue = new Issue
        {
            Id = id,
            ProjectId = projectId,
            Number = number,
            Title = title,
            Body = body,
            Priority = priority,
            Risk = risk,
            RepositoryRef = repositoryRef,
            IsDraft = isDraft,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        issue.ReplaceLabels(labels ?? new Dictionary<string, string>(StringComparer.Ordinal), recordEvent: false);
        issue.RecordEvent(new IssueCreated(
            Title: title,
            Priority: priority,
            Labels: issue.SnapshotLabels(),
            Risk: risk,
            RepositoryRef: repositoryRef));
        return issue;
    }

    public void Update(string? title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, DateTime? now = null)
    {
        var changed = false;
        var labelsChanged = false;
        if (title != null)
        {
            var nextTitle = RequireTitle(title);
            if (!string.Equals(_title, nextTitle, StringComparison.Ordinal))
            {
                _title = nextTitle;
                changed = true;
            }
        }
        if (body != null && !string.Equals(_body, body, StringComparison.Ordinal))
        {
            _body = body;
            changed = true;
        }
        if (labels != null)
        {
            labelsChanged = !LabelsMatch(labels);
            ReplaceLabels(labels, recordEvent: true, now: now);
            changed = changed || labelsChanged;
        }
        if (priority != null)
        {
            var oldPriority = _priority.Value;
            var newPriority = IssuePriority.From(priority).Value;
            _priority = IssuePriority.From(priority);
            if (oldPriority != newPriority)
            {
                RecordEvent(new IssuePriorityChanged(oldPriority, newPriority));
                changed = true;
            }
        }
        if (changed && !labelsChanged) Touch(now);
    }

    public void SetDraft(bool isDraft, DateTime? now = null)
    {
        if (_status == IssueStatus.InProgress || _status == IssueStatus.Done || _status == IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} has started and can no longer change draft state");
        if (_isDraft == isDraft) return;
        var oldIsDraft = _isDraft;
        _isDraft = isDraft;
        Touch(now);
        RecordEvent(new IssueDraftChanged(oldIsDraft, isDraft));
    }

    public void StartWorkflow(string wrId, DateTime? now = null)
    {
        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_activeWorkflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_activeWorkflowRunId}");
        _activeWorkflowRunId = NormalizeOptional(wrId);
        _status = IssueStatus.InProgress;
        Touch(now);
        RecordEvent(new IssueWorkStarted(wrId));
    }

    public IssueStartBlocker? StartBlocker(IReadOnlySet<int>? undeliveredPrerequisites)
    {
        if (_isDraft) return new IssueStartBlocker.Draft();
        if (undeliveredPrerequisites is { Count: > 0 })
        {
            foreach (var number in _prerequisiteNumbers)
            {
                if (undeliveredPrerequisites.Contains(number))
                    return new IssueStartBlocker.WaitingFor(number);
            }
        }
        return null;
    }

    public bool CanStart(IReadOnlySet<int>? undeliveredPrerequisites) =>
        StartBlocker(undeliveredPrerequisites) is null;

    public void Start(string wrId, IReadOnlySet<int>? undeliveredPrerequisites, DateTime? now = null)
    {
        var blocker = StartBlocker(undeliveredPrerequisites);
        if (blocker is IssueStartBlocker.Draft)
            throw new IssueStartBlockedException(blocker, $"Issue #{Number} is still a draft and cannot be started");
        if (blocker is IssueStartBlocker.WaitingFor waiting)
            throw new IssueStartBlockedException(blocker, $"Issue #{Number} is waiting for prerequisite issue #{waiting.PrerequisiteNumber}");

        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_activeWorkflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_activeWorkflowRunId}");

        _activeWorkflowRunId = NormalizeOptional(wrId);
        _status = IssueStatus.InProgress;
        Touch(now);
        RecordEvent(new IssueWorkStarted(wrId));
    }

    public void ClearStoppedWorkflow(string workflowRunId, DateTime? now = null)
    {
        if (_activeWorkflowRunId != workflowRunId) return;
        _activeWorkflowRunId = null;
        Touch(now);
    }

    public bool Complete(string workflowRunId, DateTime? now = null)
    {
        if (_activeWorkflowRunId != workflowRunId) return false;
        if (_status == IssueStatus.Done) return false;
        if (_status != IssueStatus.InProgress)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only InProgress can complete");
        _status = IssueStatus.Done;
        Touch(now);
        RecordEvent(new IssueWorkCompleted(workflowRunId));
        return true;
    }

    public void Archive(DateTime? now = null)
    {
        if (_status != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only Done can archive");
        var archivedAt = now ?? DateTime.UtcNow;
        _archivedAt = archivedAt;
        _activeWorkflowRunId = null;
        Touch(archivedAt);
        RecordEvent(new IssueArchived());
    }

    public void Unarchive(DateTime? now = null)
    {
        var wasArchived = _archivedAt is not null;
        _archivedAt = null;
        Touch(now);
        if (wasArchived) RecordEvent(new IssueUnarchived());
    }

    public void Close(string? reason = null, DateTime? now = null)
    {
        if (_status == IssueStatus.Done || _archivedAt != null)
            throw new InvalidOperationException($"Issue #{Number} cannot close");
        _status = IssueStatus.Cancelled;
        _activeWorkflowRunId = null;
        Touch(now);
        RecordEvent(new IssueClosed(reason));
    }

    public void Reopen(DateTime? now = null)
    {
        if (_status != IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is not cancelled");
        _status = IssueStatus.Backlog;
        Touch(now);
        RecordEvent(new IssueReopened());
    }
}
