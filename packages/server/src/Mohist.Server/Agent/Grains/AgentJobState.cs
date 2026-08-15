using Mohist.Server.Sessions.Domain;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

[GenerateSerializer]
public sealed class AgentJobState
{
    [Id(0)] public AgentJobStatus Status { get; set; } = AgentJobStatus.Pending;
    [Id(1)] public string? RunnerId { get; set; }
    [Id(2)] public string? WorkId { get; set; }
    [Id(3)] public string? FailureReason { get; set; }
    [Id(4)] public AgentJobTerminalResult? TerminalResult { get; set; }
    [Id(5)] public AgentJobInput? Input { get; set; }
    [Id(6)] public DateTimeOffset? SubmittedAt { get; set; }
    [Id(7)] public DateTimeOffset? RunningSince { get; set; }
    [Id(8)] public TimeSpan NextDispatchDelay { get; set; }
    [Id(9)] public int DispatchAttempts { get; set; }
    [Id(10)] public string? AgentConfigJson { get; set; }
    [Id(11)] public bool RunnerAccepted { get; set; }
    [Id(12)] public string? RuntimeSessionId { get; set; }
    [Id(13)] public PendingSessionClose? PendingSessionClose { get; set; }
    [Id(14)] public RoutedAgentLaunchPlan? RoutedPlan { get; set; }
    [Id(15)] public bool LaunchReady { get; set; }
    [Id(16)] public DateTimeOffset? TerminalAt { get; set; }
    [Id(17)] public PendingFailureEvent? PendingFailureEvent { get; set; }
    /// <summary>
    /// Durable record of the manual-launch preparation command the
    /// coordinator used to materialise this job. Populated by
    /// <see cref="IAgentJobGrain.PrepareManualLaunchAsync"/>; the
    /// canonical <see cref="Input"/> is built from this snapshot so
    /// reminder-driven recovery can re-derive the same args verbatim.
    /// </summary>
    [Id(18)] public PrepareManualLaunchCommand? ManualPlan { get; set; }
    [Id(19)] public PendingTerminalDeliveryEvent? PendingTerminalDeliveryEvent { get; set; }
    [Id(20)] public string? ConcurrencyPermitToken { get; set; }
    [Id(21)] public bool ConcurrencyPermitHeld { get; set; }
    [Id(22)] public string? WaitingReason { get; set; }
    [Id(23)] public DateTimeOffset? ReadySince { get; set; }
    [Id(24)] public AgentLaunchVisibility LaunchVisibility { get; set; } = AgentLaunchVisibility.Visible;
    [Id(25)] public PendingSubagentTerminalEvent? PendingSubagentTerminalEvent { get; set; }
    [Id(26)] public string? ConcurrencyPermitId { get; set; }
    [Id(27)] public string? ConcurrencyDispatchId { get; set; }
    [Id(28)] public long ConcurrencyGeneration { get; set; }
    [Id(29)] public AgentConcurrencyPermitStatus ConcurrencyGateStatus { get; set; } = AgentConcurrencyPermitStatus.DispatchPending;
    [Id(30)] public bool ConcurrencyReleasePending { get; set; }
    [Id(31)] public string? ConcurrencyWaiterId { get; set; }
    [Id(32)] public PendingInitialTurnTerminalDelivery? PendingInitialTurnTerminalDelivery { get; set; }
    [Id(33)] public AgentJobTerminalLogOwnership? TerminalLogOwnership { get; set; }
    /// <summary>
    /// Absolute deadline for a runner-loss recovery projection. This stays
    /// separate from the job timeout so the reminder can re-derive the
    /// recovery decision after activation or a silo restart.
    /// </summary>
    [Id(34)] public DateTimeOffset? RecoveryDeadlineAt { get; set; }
    [Id(35)] public PendingUpdateInterruptionEvent? PendingUpdateInterruptionEvent { get; set; }
    [Id(36)] public string? UpdateOperationId { get; set; }
    /// <summary>
    /// Monotonic replacement generation. Generation zero is the original
    /// dispatch; every accepted update interruption allocates the next value.
    /// </summary>
    [Id(37)] public int RecoveryGeneration { get; set; }
    /// <summary>
    /// The work identity currently fenced by the latest update operation.
    /// It remains recorded while a replacement is pending so reconciliation
    /// can never mistake the old execution for the new one.
    /// </summary>
    [Id(38)] public string? InterruptedWorkId { get; set; }
    [Id(39)] public List<AgentJobRecoveryAttempt> RecoveryAttempts { get; set; } = [];
    /// <summary>
    /// Durable receipt acknowledgement ledger. It is checked before current
    /// lifecycle state so exact replay returns the original acknowledgement
    /// even after a replacement or recovery-terminal transition.
    /// </summary>
    [Id(40)] public List<AppliedRuntimeRecoveryReceipt> AppliedRecoveryReceipts { get; set; } = [];
    [Id(41)] public string? RecoveryTerminalReason { get; set; }
}


[GenerateSerializer]
public sealed record AgentJobRecoveryAttempt(
    [property: Id(0)] int RecoveryGeneration,
    [property: Id(1)] string WorkId,
    [property: Id(2)] string? RunnerId,
    [property: Id(3)] string? AgentSessionId,
    [property: Id(4)] string? InputId,
    [property: Id(5)] string? TurnId,
    [property: Id(6)] string? Runtime,
    [property: Id(7)] string? RuntimeSessionId,
    [property: Id(8)] AgentJobStatus Status,
    [property: Id(9)] DateTimeOffset RecordedAt);


[GenerateSerializer]
public sealed record AgentJobTerminalLogOwnership(
    [property: Id(0)] string OwnerKind,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] string WorkId,
    [property: Id(3)] string RunnerId);
