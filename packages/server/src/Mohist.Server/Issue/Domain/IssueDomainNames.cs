namespace Mohist.Server.Issue.Domain;

public static class IssueDomainNames
{
    public static string StatusName(IssueStatus status) => status switch
    {
        IssueStatus.InProgress => "in_progress",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string Health(IssueStatus status, IssueAttention? attention) => status switch
    {
        IssueStatus.Done => "done",
        IssueStatus.Cancelled => "cancelled",
        _ when attention?.Reason is IssueAttentionReason.Blocked or IssueAttentionReason.WorkflowFailed => "blocked",
        _ when attention is not null => "attention",
        _ => "active",
    };
}
