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
        string[]? labels = null,
        string priority = "p2",
        string? repositoryRef = null,
        string? risk = null,
        DateTime? now = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        var labelsCopy = labels ?? [];
        var issue = new Issue
        {
            Id = id,
            ProjectId = projectId,
            Number = number,
            Title = title,
            Body = body,
            Labels = labelsCopy,
            Priority = priority,
            Risk = risk,
            RepositoryRef = repositoryRef,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        issue.RecordEvent(new IssueCreated(
            Title: title,
            Priority: priority,
            Labels: [.. labelsCopy],
            Risk: risk,
            RepositoryRef: repositoryRef));
        return issue;
    }

    public void Update(string? title, string? body, string[]? labels, string? priority, DateTime? now = null)
    {
        if (title != null) _title = RequireTitle(title);
        if (body != null) _body = body;
        if (labels != null)
        {
            var oldLabels = _labels;
            _labels = labels ?? [];
            if (!oldLabels.SequenceEqual(_labels))
                RecordEvent(new IssueLabelsChanged([.. oldLabels], [.. _labels]));
        }
        if (priority != null)
        {
            var oldPriority = _priority.Value;
            var newPriority = IssuePriority.From(priority).Value;
            _priority = IssuePriority.From(priority);
            if (oldPriority != newPriority)
                RecordEvent(new IssuePriorityChanged(oldPriority, newPriority));
        }
        Touch(now);
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
