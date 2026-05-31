namespace Mohist.Server.Issue.Domain;

public sealed class IssueAttention
{
    public IssueAttentionReason Reason { get; init; } = IssueAttentionReason.Blocked;
    public string? Message { get; init; }
    public string Source { get; init; } = "system";
    public string? WorkflowRunId { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public string[] AvailableActions { get; init; } = [];

    public static IssueAttention ReviewRequired(string? workflowRunId, string? message = null) => new()
    {
        Reason = IssueAttentionReason.ReviewRequired,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["approve", "request_changes"],
    };

    public static IssueAttention Blocked(string? workflowRunId, string? message = null) => new()
    {
        Reason = IssueAttentionReason.Blocked,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["retry", "cancel"],
    };
}