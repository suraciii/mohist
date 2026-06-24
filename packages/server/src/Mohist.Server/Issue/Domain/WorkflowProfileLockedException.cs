namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Thrown by the issue grain when a caller tries to change an issue's
/// workflow profile selection on an issue that has already started a
/// workflow. The issue's execution template is an execution fact and
/// must not be silently re-templated while a run is in flight. The route
/// layer translates this into a 409 response with a clear reason.
/// </summary>
public sealed class WorkflowProfileLockedException : InvalidOperationException
{
    public string IssueNumber { get; }
    public string? ActiveWorkflowRunId { get; }

    public WorkflowProfileLockedException(int issueNumber, string? activeWorkflowRunId)
        : base(BuildMessage(issueNumber, activeWorkflowRunId))
    {
        IssueNumber = issueNumber.ToString();
        ActiveWorkflowRunId = activeWorkflowRunId;
    }

    private static string BuildMessage(int issueNumber, string? activeWorkflowRunId) =>
        activeWorkflowRunId is null
            ? $"Issue #{issueNumber} has started and its workflow profile selection cannot be changed"
            : $"Issue #{issueNumber} has an active workflow run ({activeWorkflowRunId}) and its workflow profile selection cannot be changed";
}
