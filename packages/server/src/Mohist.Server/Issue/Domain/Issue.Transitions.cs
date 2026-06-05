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
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_activeWorkflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_activeWorkflowRunId}");
        _activeWorkflowRunId = NormalizeOptional(wrId);
        _status = IssueStatus.InProgress;
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
        return true;
    }

    public void Archive(DateTime? now = null)
    {
        if (_status != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only Done can archive");
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
            throw new InvalidOperationException($"Issue #{Number} cannot close");
        _status = IssueStatus.Cancelled;
        Touch(now);
    }

    public void Reopen(DateTime? now = null)
    {
        if (_status != IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is not cancelled");
        _status = IssueStatus.Backlog;
        Touch(now);
    }
}
