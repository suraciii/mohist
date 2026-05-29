using Mohist.Server.Project.Queries;

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

        public void MarkReady()
        {
            if (issue.Stage == IssueStage.Cancelled)
                throw new InvalidOperationException($"Issue #{issue.Number} is cancelled");
            issue.Stage = IssueStage.Todo;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void StartWorkflow(string wrId)
        {
            if (issue.Stage == IssueStage.Cancelled || issue.Stage == IssueStage.Done)
                throw new InvalidOperationException($"Issue #{issue.Number} is {issue.Stage}");
            issue.WorkflowRunId = wrId;
            issue.Stage = IssueStage.InProgress;
            issue.Attention = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void Complete()
        {
            issue.Stage = IssueStage.Done;
            issue.Attention = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void Archive()
        {
            if (issue.Stage != IssueStage.Done)
                throw new InvalidOperationException($"Issue #{issue.Number} is {issue.Stage}, only Done can archive");
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
            if (issue.Stage == IssueStage.Done || issue.ArchivedAt != null)
                throw new InvalidOperationException($"Issue #{issue.Number} cannot close");
            issue.Stage = IssueStage.Cancelled;
            issue.WorkflowRunId = null;
            issue.Attention = null;
            issue.UpdatedAt = DateTime.UtcNow;
        }

        public void Reopen()
        {
            if (issue.Stage != IssueStage.Cancelled)
                throw new InvalidOperationException($"Issue #{issue.Number} is not cancelled");
            issue.Stage = IssueStage.Backlog;
            issue.UpdatedAt = DateTime.UtcNow;
        }
    }
}