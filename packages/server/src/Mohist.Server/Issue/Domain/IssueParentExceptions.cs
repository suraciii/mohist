namespace Mohist.Server.Issue.Domain;

public sealed class IssueChildCannotJoinEpicException(int issueNumber)
    : InvalidOperationException($"Issue #{issueNumber} is a sub-issue and cannot join an Epic");

public sealed class IssueEpicMemberCannotBecomeChildException(int issueNumber, int epicNumber)
    : InvalidOperationException($"Issue #{issueNumber} belongs to Epic #{epicNumber} and cannot become a sub-issue");

public sealed class IssueSelfParentException(int issueNumber)
    : InvalidOperationException($"Issue #{issueNumber} cannot be its own parent");

public sealed class IssueCannotBecomeChildException(int issueNumber, IssueStatus status, bool hasWorkflowStarted)
    : InvalidOperationException($"Issue #{issueNumber} cannot become a sub-issue in status {status} (workflow started: {hasWorkflowStarted})");

public sealed class IssueParentNotFoundException(int parentNumber)
    : KeyNotFoundException($"Parent issue #{parentNumber} not found");

public sealed class IssueParentIneligibleException(int parentNumber)
    : InvalidOperationException($"Parent issue #{parentNumber} is not an eligible Backlog issue");

public sealed class IssueParentIsChildException(int parentNumber)
    : InvalidOperationException($"Issue #{parentNumber} is already a sub-issue and cannot have children");

public sealed class IssueHasChildrenCannotBecomeChildException(int issueNumber)
    : InvalidOperationException($"Issue #{issueNumber} has children and cannot become a sub-issue");
