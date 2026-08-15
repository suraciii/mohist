using Orleans;

namespace Mohist.Server.Workflow.Domain.Run;

public enum AgentResultSettlementState
{
    AwaitingResult,
    RecoverablyInterrupted,
    Unknown,
    Blocked
}

/// <summary>
/// Physical facts observed for a Workflow-owned Agent execution. They do not
/// establish an authoritative task result.
/// </summary>
public enum AgentExecutionObservationKind
{
    Idle,
    Completed,
    Failed,
    Cancelled,
    Unknown,
    Stopped,
    StopUnconfirmed,
    TargetMissing,
    Disconnected
}

/// <summary>
/// The Workflow-owned arbitration record for one Agent task attempt.
/// </summary>
public sealed class AgentResultSettlement
{
    public required AgentResultSettlementState State { get; set; }
    public required string TaskRunId { get; init; }
    public required string WorkId { get; init; }
    public required string RunnerId { get; init; }
    public string? AgentSessionId { get; set; }
    public string? AgentTurnId { get; set; }
    public string? Runtime { get; set; }
    public string? RuntimeSessionId { get; set; }
    public string? StopOperationId { get; set; }
    public string? UpdateOperationId { get; set; }
    public AgentExecutionObservationKind? LastObservation { get; set; }
    public string? ReasonCode { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset? FirstUnknownAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
}

/// <summary>
/// Immutable physical identity that Workflow must fence before accepting an
/// AgentSession execution fact.
/// </summary>
[GenerateSerializer]
public sealed record AgentExecutionBinding(
    [property: Id(0)] string TaskRunId,
    [property: Id(1)] string WorkId,
    [property: Id(2)] string RunnerId,
    [property: Id(3)] string AgentSessionId,
    [property: Id(4)] string AgentTurnId,
    [property: Id(5)] string Runtime,
    [property: Id(6)] string RuntimeSessionId);

[GenerateSerializer]
public sealed record AgentExecutionObservation(
    [property: Id(0)] AgentExecutionBinding Binding,
    [property: Id(1)] AgentExecutionObservationKind Kind,
    [property: Id(2)] string ReasonCode,
    [property: Id(3)] string? Message = null,
    [property: Id(4)] string? StopOperationId = null);

/// <summary>
/// Distinguishes a rejected identity from an accepted replay that left the
/// aggregate unchanged, so grain commands do not rewrite state on replay.
/// </summary>
public enum AgentExecutionUpdate
{
    Rejected,
    Unchanged,
    Updated
}

public sealed record WorkflowReportableTaskAttempt(
    string Stage,
    string TaskRunId,
    string WorkId,
    string RunnerId,
    AgentResultSettlementState? SettlementState);
