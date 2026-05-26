namespace Mohist.Server.Issue.Domain;

public enum IssueStage
{
    Backlog,
    Todo,
    InProgress,
    Done,
    Cancelled
}

public static class IssueAttentionReasons
{
    public const string ReviewRequired = "review_required";
    public const string Blocked = "blocked";
    public const string MergeConflict = "merge_conflict";
    public const string ApprovalRejected = "approval_rejected";
    public const string MissingPrerequisite = "missing_prerequisite";
    public const string WorkflowFailed = "workflow_failed";
    public const string Paused = "paused";
}

public sealed class IssueAttention
{
    public string Reason { get; set; } = IssueAttentionReasons.Blocked;
    public string? Message { get; set; }
    public string Source { get; set; } = "system";
    public string? WorkflowRunId { get; set; }
    public string RequestedAt { get; set; } = DateTime.UtcNow.ToString("O");
    public string[] AvailableActions { get; set; } = [];

    public static IssueAttention ReviewRequired(string? workflowRunId, string? message = null) => new()
    {
        Reason = IssueAttentionReasons.ReviewRequired,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["approve", "request_changes"],
    };

    public static IssueAttention Blocked(string? workflowRunId, string? message = null) => new()
    {
        Reason = IssueAttentionReasons.Blocked,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["retry", "cancel"],
    };
}
