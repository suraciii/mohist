namespace Mohist.Server.Sessions.Domain;

public enum SpawnRequestFenceOutcome
{
    ValidationPending,
    PreplanRejected,
    Admitted,
}

public enum SessionParentLinkState
{
    Attached,
    Detached,
}

public enum TerminalReportState
{
    None,
    Pending,
    Delivered,
    Suppressed,
}

public enum LinkReservationState
{
    Reserved,
    Attached,
    Rejected,
}

public enum AgentLaunchVisibility
{
    Provisional,
    Visible,
    Rejected,
}

public enum SessionTreeMutationKind
{
    Attach,
    Detach,
    CascadeStop,
}

[GenerateSerializer]
public sealed record SessionParentLink(
    [property: Id(0)] string EdgeId,
    [property: Id(1)] string ParentSessionId,
    [property: Id(2)] string ParentAgentId,
    [property: Id(3)] string ChildLaunchJobId,
    [property: Id(4)] DateTimeOffset AttachedAt,
    [property: Id(5)] long AttachedRevision,
    [property: Id(6)] SessionParentLinkState State,
    [property: Id(7)] DateTimeOffset? DetachedAt = null,
    [property: Id(8)] long? DetachedRevision = null,
    [property: Id(9)] TerminalReportState TerminalReport = TerminalReportState.None,
    [property: Id(10)] string? TerminalReportDeliveredInputId = null);

[GenerateSerializer]
public sealed record LinkReservation(
    [property: Id(0)] string EdgeId,
    [property: Id(1)] string ParentSessionId,
    [property: Id(2)] string ChildSessionId,
    [property: Id(3)] LinkReservationState State,
    [property: Id(4)] string? RejectionReason = null,
    [property: Id(5)] long? AttachedRevision = null);

[GenerateSerializer]
public sealed record PendingSessionTreeMutation(
    [property: Id(0)] string CommandId,
    [property: Id(1)] SessionTreeMutationKind Kind,
    [property: Id(2)] long AssignedRevision,
    [property: Id(3)] string EdgeId,
    [property: Id(4)] string ParentSessionId,
    [property: Id(5)] string ChildSessionId,
    [property: Id(6)] string? ExpectedWorkDir,
    [property: Id(7)] string? ExpectedRunnerId,
    [property: Id(8)] string? ExpectedRuntime,
    [property: Id(9)] string? ExpectedRuntimeSessionId,
    [property: Id(10)] bool StopAdmissionActive = false);

[GenerateSerializer]
public sealed record SessionTreeMutationFence(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] long GraphRevision,
    [property: Id(2)] LinkReservation? Reservation = null,
    [property: Id(3)] PendingSessionTreeMutation? PendingMutation = null,
    [property: Id(4)] bool ActiveTreeStop = false,
    [property: Id(5)] IReadOnlyList<LinkReservation>? Reservations = null,
    [property: Id(6)] IReadOnlyList<PendingSessionTreeMutation>? PendingMutations = null);

[GenerateSerializer]
public sealed record ReserveSessionTreeLinkCommand(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] string ParentSessionId,
    [property: Id(3)] string ChildSessionId,
    [property: Id(4)] string? ExpectedWorkDir,
    [property: Id(5)] string? ExpectedRunnerId,
    [property: Id(6)] string? ExpectedRuntime,
    [property: Id(7)] string? ExpectedRuntimeSessionId,
    [property: Id(8)] string CommandId);

[GenerateSerializer]
public sealed record SessionTreeMutationResult(
    [property: Id(0)] string EdgeId,
    [property: Id(1)] long Revision,
    [property: Id(2)] LinkReservationState State,
    [property: Id(3)] string? RejectionReason = null);

[GenerateSerializer]
public sealed record SpawnRequestFence(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string ParentSessionId,
    [property: Id(2)] string IdempotencyKey,
    [property: Id(3)] string RequestFingerprint,
    [property: Id(4)] SpawnRequestFenceOutcome Outcome,
    [property: Id(5)] string? PreplanRejectionReason = null);
