using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public sealed class WorkflowAttention
{
    public WorkflowAttentionReason Reason { get; init; } = WorkflowAttentionReason.Blocked;
    public string State { get; init; } = "attention";
    public string? Message { get; init; }
    public string? ReasonCode { get; init; }
    public string? WorkId { get; init; }
    public string? OwnerId { get; init; }
    public DateTimeOffset? RecoveryDeadlineAt { get; init; }
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

    /// <summary>
    /// Non-failure blocked attention for an unresolved Agent result. The
    /// stable <see cref="WorkflowAttentionReason.AgentResultUnconfirmed"/>
    /// reason stays the consumer category; the settlement's original persisted
    /// reason code and deadline ride along so the Issue read model observes
    /// them without conflating the block with a failure or cancellation.
    /// </summary>
    public static WorkflowAttention AgentResultUnconfirmed(
        string? workflowRunId,
        string? message = null,
        string? reasonCode = null,
        DateTimeOffset? deadlineAt = null) => new()
    {
        Reason = WorkflowAttentionReason.AgentResultUnconfirmed,
        State = "blocked",
        Message = message,
        ReasonCode = reasonCode,
        RecoveryDeadlineAt = deadlineAt,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        AvailableActions = ["stop"],
    };

    public static WorkflowAttention RecoverableInterrupted(
        string? workflowRunId,
        WorkInterruptionView interruption) => new()
    {
        Reason = WorkflowAttentionReason.RecoverableInterrupted,
        State = "recoverable-interrupted",
        Message = $"Recoverable interruption: {interruption.ReasonCode}",
        ReasonCode = interruption.ReasonCode,
        WorkId = interruption.WorkId,
        OwnerId = interruption.OwnerId,
        RecoveryDeadlineAt = interruption.RecoveryDeadlineAt,
        Source = "workflow",
        WorkflowRunId = workflowRunId,
        RequestedAt = interruption.RecordedAt.UtcDateTime,
        AvailableActions = [],
    };
}
