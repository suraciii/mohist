namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public sealed class WorkflowAttention
{
    public WorkflowAttentionReason Reason { get; init; } = WorkflowAttentionReason.Blocked;
    public string? Message { get; init; }
    public string Source { get; init; } = "system";
    public string? WorkflowRunId { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public string[] AvailableActions { get; init; } = [];

    public static WorkflowAttention ReviewRequired(string? workflowRunId, string? message = null) => new()
    {
        Reason = WorkflowAttentionReason.ReviewRequired,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["approve", "request_changes"],
    };

    public static WorkflowAttention Blocked(string? workflowRunId, string? message = null) => new()
    {
        Reason = WorkflowAttentionReason.Blocked,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["retry", "cancel"],
    };

    public static WorkflowAttention AgentResultUnconfirmed(string? workflowRunId, string? message = null) => new()
    {
        Reason = WorkflowAttentionReason.Blocked,
        Message = message,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["stop"],
    };
}
