using Mohist.Server.Infrastructure.Errors;

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
        DateTime? now = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        return new Issue
        {
            Id = id,
            ProjectId = projectId,
            Number = number,
            Title = title,
            Body = body,
            Labels = labels ?? [],
            Priority = priority,
            RepositoryRef = repositoryRef,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    public void Update(string? title, string? body, string[]? labels, string? priority, DateTime? now = null)
    {
        if (title != null) _title = RequireTitle(title);
        if (body != null) _body = body;
        if (labels != null) _labels = labels;
        if (priority != null) _priority = IssuePriority.From(priority);
        Touch(now);
    }

    public void StartWorkflow(string wrId, DateTime? now = null)
    {
        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new DomainConflictException($"Issue #{Number} is {_status}");
        if (_activeWorkflowRunId is not null)
            throw new DomainConflictException($"Issue #{Number} already has workflow {_activeWorkflowRunId}");
        _activeWorkflowRunId = NormalizeOptional(wrId);
        _status = IssueStatus.InProgress;
        Touch(now);
    }

    public bool Complete(string workflowRunId, DateTime? now = null)
    {
        if (_activeWorkflowRunId != workflowRunId) return false;
        if (_status == IssueStatus.Done) return false;
        if (_status != IssueStatus.InProgress)
            throw new DomainConflictException($"Issue #{Number} is {_status}, only InProgress can complete");
        _status = IssueStatus.Done;
        _activeWorkflowRunId = null;
        Touch(now);
        return true;
    }

    public void Archive(DateTime? now = null)
    {
        if (_status != IssueStatus.Done)
            throw new DomainConflictException($"Issue #{Number} is {_status}, only Done can archive");
        var archivedAt = now ?? DateTime.UtcNow;
        _archivedAt = archivedAt;
        Touch(archivedAt);
    }

    public void Unarchive(DateTime? now = null)
    {
        _archivedAt = null;
        Touch(now);
    }

    public void Close(DateTime? now = null)
    {
        if (_status == IssueStatus.Done || _archivedAt != null)
            throw new DomainConflictException($"Issue #{Number} cannot close");
        _status = IssueStatus.Cancelled;
        _activeWorkflowRunId = null;
        Touch(now);
    }

    public void Reopen(DateTime? now = null)
    {
        if (_status != IssueStatus.Cancelled)
            throw new DomainConflictException($"Issue #{Number} is not cancelled");
        _status = IssueStatus.Backlog;
        Touch(now);
    }

    /// <summary>
    /// Idempotent transition invoked by the workflow lifecycle hook when the
    /// workflow ends in Failed or Stopped. No-op if the workflow run id does
    /// not match the current active run, or the issue is no longer InProgress
    /// (e.g. another path already closed it). This makes the hook safe to
    /// dispatch multiple times for the same terminal event.
    /// </summary>
    public bool AbortWorkflow(string workflowRunId, DateTime? now = null)
    {
        if (_activeWorkflowRunId != workflowRunId) return false;
        if (_status == IssueStatus.Cancelled) return false;
        if (_status != IssueStatus.InProgress) return false;
        _status = IssueStatus.Cancelled;
        _activeWorkflowRunId = null;
        Touch(now);
        return true;
    }
}
