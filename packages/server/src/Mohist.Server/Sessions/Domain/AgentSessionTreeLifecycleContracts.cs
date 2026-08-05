namespace Mohist.Server.Sessions.Domain;

public enum SubagentTerminalReportClaimDisposition
{
    ClaimedPending,
    Pending,
    Delivered,
    Suppressed,
    Rejected,
}

public enum SubagentTerminalReportDeliveryDisposition
{
    Delivered,
    AlreadyDelivered,
    InputIdConflict,
    Rejected,
}

public enum SessionTreeDetachMutationState
{
    Pending,
    Acknowledged,
    Detached,
    ReconciliationRequired,
    Rejected,
}

public enum SessionTreeAttachMutationState
{
    Pending,
    Acknowledged,
    Attached,
    ReconciliationRequired,
    Rejected,
}

public enum SessionTreeBindingAcquireState
{
    Acquired,
    AlreadyAcquired,
    BindingChanged,
    ReconciliationRequired,
}

public enum SessionTreeBindingReleaseState
{
    Released,
    AlreadyReleased,
    ReconciliationRequired,
}

public enum SessionTreeStopSnapshotPhase
{
    Materializing,
    Frozen,
}

public enum SessionTreeStopSnapshotDisposition
{
    Started,
    Replayed,
    Blocked,
    AdmissionBlocked,
    Rejected,
}

public enum SessionTreeStopAdmissionOutcome
{
    Running,
    Unknown,
    Completed,
    Partial,
}

public static class SubagentTerminalReportIdempotencyKeys
{
    public static string For(string edgeId, string childLaunchJobId) =>
        $"subagent-terminal:{edgeId}:{childLaunchJobId}";
}

[GenerateSerializer]
public sealed record ClaimSubagentTerminalReportCommand(
    [property: Id(0)] string EdgeId,
    [property: Id(1)] string ChildLaunchJobId);

[GenerateSerializer]
public sealed record ClaimSubagentTerminalReportResult(
    [property: Id(0)] SubagentTerminalReportClaimDisposition Disposition,
    [property: Id(1)] string? DeliveredInputId = null,
    [property: Id(2)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record RecordSubagentTerminalReportDeliveredCommand(
    [property: Id(0)] string EdgeId,
    [property: Id(1)] string ChildLaunchJobId,
    [property: Id(2)] string ParentInputId);

[GenerateSerializer]
public sealed record RecordSubagentTerminalReportDeliveredResult(
    [property: Id(0)] SubagentTerminalReportDeliveryDisposition Disposition,
    [property: Id(1)] string? DeliveredInputId = null,
    [property: Id(2)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record ApplyParentLinkDetachCommand(
    [property: Id(0)] string EdgeId,
    [property: Id(1)] string ParentSessionId,
    [property: Id(2)] string ChildLaunchJobId,
    [property: Id(3)] long DetachedRevision,
    [property: Id(4)] string? CommandId = null,
    [property: Id(5)] string? ChildSessionId = null,
    [property: Id(6)] long? ExpectedAttachedRevision = null);

[GenerateSerializer]
public sealed record ApplyParentLinkDetachResult(
    [property: Id(0)] SessionTreeDetachMutationState State,
    [property: Id(1)] SessionParentLink? Link = null,
    [property: Id(2)] string? RejectionReason = null,
    [property: Id(3)] SessionTreeDetachReceipt? Receipt = null);

[GenerateSerializer]
public sealed record ApplyParentLinkAttachCommand(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] string ParentSessionId,
    [property: Id(3)] string ParentAgentId,
    [property: Id(4)] string ChildLaunchJobId,
    [property: Id(5)] long AttachedRevision,
    [property: Id(6)] string? ExpectedWorkDir,
    [property: Id(7)] string? ExpectedRunnerId,
    [property: Id(8)] string? ExpectedRuntime,
    [property: Id(9)] string? ExpectedRuntimeSessionId,
    [property: Id(10)] string ProjectId = "",
    [property: Id(11)] long ExpectedBindingEpoch = 0,
    [property: Id(12)] string BindingUseReceiptId = "",
    [property: Id(13)] SessionTreeExpectedLinkState ExpectedLinkState = SessionTreeExpectedLinkState.Absent);

[GenerateSerializer]
public sealed record ApplyParentLinkAttachResult(
    [property: Id(0)] SessionTreeAttachMutationState State,
    [property: Id(1)] SessionParentLink? Link = null,
    [property: Id(2)] SessionTreeAttachReceipt? Receipt = null,
    [property: Id(3)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record BeginSessionTreeDetachCommand(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] string ParentSessionId,
    [property: Id(3)] string ChildSessionId,
    [property: Id(4)] string CommandId,
    [property: Id(5)] string ChildLaunchJobId,
    [property: Id(6)] long ExpectedAttachedRevision);

[GenerateSerializer]
public sealed record SessionTreeDetachMutationResult(
    [property: Id(0)] SessionTreeDetachMutationState State,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] long Revision,
    [property: Id(3)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record SessionTreeDetachReceipt(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] string ParentSessionId,
    [property: Id(3)] string ChildSessionId,
    [property: Id(4)] long Revision,
    [property: Id(5)] string ChildLaunchJobId,
    [property: Id(6)] long ExpectedAttachedRevision);

[GenerateSerializer]
public sealed record SessionTreeAttachReceipt(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] string ParentSessionId,
    [property: Id(3)] string ChildSessionId,
    [property: Id(4)] string ChildLaunchJobId,
    [property: Id(5)] long Revision,
    [property: Id(6)] string ProjectId = "",
    [property: Id(7)] SessionTreeMutationKind MutationKind = SessionTreeMutationKind.Attach,
    [property: Id(8)] string? ParentWorkDir = null,
    [property: Id(9)] string? RunnerId = null,
    [property: Id(10)] string? Runtime = null,
    [property: Id(11)] string? RuntimeSessionId = null,
    [property: Id(12)] long BindingEpoch = 0,
    [property: Id(13)] string BindingUseReceiptId = "",
    [property: Id(14)] SessionTreeExpectedLinkState ExpectedLinkState = SessionTreeExpectedLinkState.Absent,
    [property: Id(15)] string ParentAgentId = "");

[GenerateSerializer]
public sealed record AcquireChildAttachBindingCommand(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string CommandId,
    [property: Id(2)] string EdgeId,
    [property: Id(3)] string ParentSessionId,
    [property: Id(4)] string? ExpectedWorkDir,
    [property: Id(5)] string? ExpectedRunnerId,
    [property: Id(6)] string? ExpectedRuntime,
    [property: Id(7)] string? ExpectedRuntimeSessionId,
    [property: Id(8)] long ExpectedBindingEpoch,
    [property: Id(9)] string ParentAgentId = "");

[GenerateSerializer]
public sealed record SessionTreeBindingUseReceipt(
    [property: Id(0)] string ReceiptId,
    [property: Id(1)] string ProjectId,
    [property: Id(2)] string CommandId,
    [property: Id(3)] string EdgeId,
    [property: Id(4)] string ParentSessionId,
    [property: Id(5)] string? ParentWorkDir,
    [property: Id(6)] string? RunnerId,
    [property: Id(7)] string? Runtime,
    [property: Id(8)] string? RuntimeSessionId,
    [property: Id(9)] long BindingEpoch,
    [property: Id(10)] SessionTreeBindingUseState State = SessionTreeBindingUseState.Held,
    [property: Id(11)] string? ReleaseOutcome = null,
    [property: Id(12)] string ParentAgentId = "");

[GenerateSerializer]
public sealed record AcquireChildAttachBindingResult(
    [property: Id(0)] SessionTreeBindingAcquireState State,
    [property: Id(1)] SessionTreeBindingUseReceipt? Receipt = null,
    [property: Id(2)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record ReleaseChildAttachBindingCommand(
    [property: Id(0)] SessionTreeBindingUseReceipt Receipt,
    [property: Id(1)] string Outcome);

[GenerateSerializer]
public sealed record ReleaseChildAttachBindingResult(
    [property: Id(0)] SessionTreeBindingReleaseState State,
    [property: Id(1)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record SessionTreeStopMembership(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string? ParentSessionId,
    [property: Id(2)] string? EdgeId,
    [property: Id(3)] string? ChildLaunchJobId,
    [property: Id(4)] long AttachedRevision);

[GenerateSerializer]
public sealed record SessionTreeStopTargetSnapshot(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string? TurnId,
    [property: Id(2)] string? JobId,
    [property: Id(3)] AgentTurnStatus? TurnStatus,
    [property: Id(4)] string? RunnerId,
    [property: Id(5)] string? Runtime,
    [property: Id(6)] string? RuntimeSessionId,
    [property: Id(7)] string? WorkDir,
    [property: Id(8)] string StopOperationId);

[GenerateSerializer]
public sealed record SessionTreeStopSnapshot(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string RootSessionId,
    [property: Id(2)] string OperationId,
    [property: Id(3)] string IdempotencyKey,
    [property: Id(4)] string RequestFingerprint,
    [property: Id(5)] long GraphRevision,
    [property: Id(6)] IReadOnlyList<SessionTreeStopMembership> Membership,
    [property: Id(7)] IReadOnlyList<SessionTreeStopTargetSnapshot> Targets,
    [property: Id(8)] SessionTreeStopAdmissionOutcome AdmissionOutcome = SessionTreeStopAdmissionOutcome.Running,
    [property: Id(9)] SessionTreeStopSnapshotPhase Phase = SessionTreeStopSnapshotPhase.Frozen);

[GenerateSerializer]
public sealed record BeginSessionTreeStopSnapshotCommand(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string RootSessionId,
    [property: Id(2)] string OperationId,
    [property: Id(3)] string IdempotencyKey,
    [property: Id(4)] string RequestFingerprint);

[GenerateSerializer]
public sealed record SessionTreeStopTargetFact(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string? TurnId,
    [property: Id(2)] string? JobId,
    [property: Id(3)] AgentTurnStatus? TurnStatus,
    [property: Id(4)] string? RunnerId,
    [property: Id(5)] string? Runtime,
    [property: Id(6)] string? RuntimeSessionId,
    [property: Id(7)] string? WorkDir);

[GenerateSerializer]
public sealed record SessionTreeStopSnapshotFacts(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string RootSessionId,
    [property: Id(2)] long GraphRevision,
    [property: Id(3)] IReadOnlyList<SessionTreeStopMembership> Membership,
    [property: Id(4)] IReadOnlyList<SessionTreeStopTargetFact> Targets);

[GenerateSerializer]
public sealed record SessionTreeSessionBindingFact(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string? WorkDir,
    [property: Id(3)] string? RunnerId,
    [property: Id(4)] string? Runtime,
    [property: Id(5)] string? RuntimeSessionId,
    [property: Id(6)] long BindingEpoch);

[GenerateSerializer]
public sealed record SessionTreeStopSnapshotResult(
    [property: Id(0)] SessionTreeStopSnapshotDisposition Disposition,
    [property: Id(1)] SessionTreeStopSnapshot? Snapshot = null,
    [property: Id(2)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record SessionTreeStopAdmissionResult(
    [property: Id(0)] bool Active,
    [property: Id(1)] string? OperationId = null,
    [property: Id(2)] string? RejectionReason = null);

public static class SessionTreeStopOperationIds
{
    public static string ForTarget(string operationId, string sessionId) =>
        $"session-tree-stop:{operationId}:{sessionId}";
}
