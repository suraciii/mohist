namespace Mohist.Server.Issue.Domain;

public enum IssueStatus
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

[GenerateSerializer]
public sealed class IssueAttention
{
    [Id(0)] public string Reason { get; set; } = IssueAttentionReasons.Blocked;
    [Id(1)] public string? Message { get; set; }
    [Id(2)] public string Source { get; set; } = "system";
    [Id(3)] public string? WorkflowRunId { get; set; }
    [Id(4)] public string RequestedAt { get; set; } = DateTime.UtcNow.ToString("O");
    [Id(5)] public string[] AvailableActions { get; set; } = [];

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
