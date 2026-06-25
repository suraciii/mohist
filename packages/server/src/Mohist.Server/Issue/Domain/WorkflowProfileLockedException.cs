namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Thrown by the issue grain when a caller tries to change an issue's
/// workflow profile selection on an issue that has already started a
/// workflow. The issue's execution template is an execution fact and
/// must not be silently re-templated once a workflow run reference exists.
/// The route layer translates this into a 409 response with a clear reason.
/// </summary>
public sealed class WorkflowProfileLockedException : InvalidOperationException
{
    public string IssueNumber { get; }
    public string? WorkflowRunId { get; }

    public WorkflowProfileLockedException(int issueNumber, string? workflowRunId)
        : base(BuildMessage(issueNumber, workflowRunId))
    {
        IssueNumber = issueNumber.ToString();
        WorkflowRunId = workflowRunId;
    }

    private static string BuildMessage(int issueNumber, string? workflowRunId) =>
        workflowRunId is null
            ? $"Issue #{issueNumber} has started and its workflow profile selection cannot be changed"
            : $"Issue #{issueNumber} has a workflow run reference ({workflowRunId}) and its workflow profile selection cannot be changed";
}
