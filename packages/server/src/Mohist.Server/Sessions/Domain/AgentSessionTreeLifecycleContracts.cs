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
    [property: Id(9)] string? ExpectedRuntimeSessionId);

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
    [property: Id(5)] long Revision);

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
    [property: Id(5)] string? RuntimeSessionId);

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
