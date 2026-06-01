using Mohist.Server.Project.Domain;

namespace Mohist.Server.Issue.Domain;

public static partial class IssueExtensions
{
    extension(Issue)
    {
        public static Issue Create(
            string id,
            string projectId,
            int number,
            string title,
            string? body = null,
            string[]? labels = null,
            string priority = "p2",
            RepositoryInfo? repository = null)
        {
            return new Issue
            {
                Id = id,
                ProjectId = projectId,
                Number = number,
                Title = title,
                Body = body,
                Labels = labels ?? [],
                Priority = priority,
                Repository = repository,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }
    }

    extension(Issue issue)
    {
        public void Update(string? title, string? body, string[]? labels, string? priority)
        {
            if (title != null) issue.Title = title;
            if (body != null) issue.Body = body;
            if (labels != null) issue.Labels = labels;
            if (priority != null) issue.Priority = priority;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void StartWorkflow(string wrId)
        {
            if (issue.Status == IssueStatus.Cancelled || issue.Status == IssueStatus.Done)
                throw new InvalidOperationException($"Issue #{issue.Number} is {issue.Status}");
            if (issue.WorkflowRunId is not null)
                throw new InvalidOperationException($"Issue #{issue.Number} already has workflow {issue.WorkflowRunId}");
            issue.WorkflowRunId = wrId;
            issue.Status = IssueStatus.InProgress;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public bool Complete(string workflowRunId)
        {
            if (issue.WorkflowRunId != workflowRunId) return false;
            if (issue.Status == IssueStatus.Done) return false;
            if (issue.Status != IssueStatus.InProgress)
                throw new InvalidOperationException($"Issue #{issue.Number} is {issue.Status}, only InProgress can complete");
            issue.Status = IssueStatus.Done;
            issue.UpdatedAt = DateTime.UtcNow;
            return true;
        }

        public void Archive()
        {
            if (issue.Status != IssueStatus.Done)
                throw new InvalidOperationException($"Issue #{issue.Number} is {issue.Status}, only Done can archive");
            issue.ArchivedAt = DateTime.UtcNow;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void Unarchive()
        {
            issue.ArchivedAt = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void Close()
        {
            if (issue.Status == IssueStatus.Done || issue.ArchivedAt != null)
                throw new InvalidOperationException($"Issue #{issue.Number} cannot close");
            issue.Status = IssueStatus.Cancelled;
            issue.WorkflowRunId = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void Reopen()
        {
            if (issue.Status != IssueStatus.Cancelled)
                throw new InvalidOperationException($"Issue #{issue.Number} is not cancelled");
            issue.Status = IssueStatus.Backlog;
            issue.UpdatedAt = DateTime.UtcNow;
        }
    }
}
