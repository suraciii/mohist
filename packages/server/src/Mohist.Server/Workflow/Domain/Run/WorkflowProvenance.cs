namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Bounded vocabulary of provenance actions recorded for a WorkflowRun.
/// String constants (not an enum) so a newer producer's action survives
/// replay and projection on older readers without deserialization failure.
/// </summary>
public static class WorkflowProvenanceActions
{
    public const string ManualApproval = "manual-approval";
    public const string Retry = "retry";
    public const string Rerun = "rerun";
    public const string Recovery = "recovery";
    public const string Push = "push";
    public const string PullRequest = "pull-request";
    public const string Merge = "merge";
    public const string CiPassed = "ci-passed";
    public const string CiSkipped = "ci-skipped";
}

public static class WorkflowProvenanceActorKinds
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string System = "system";
    public const string External = "external";
}

/// <summary>
/// The initiating actor of a provenance fact, passed into workflow control
/// paths by the calling surface (API route, GitHub ingress). Automated
/// paths use <see cref="WorkflowProvenanceActorKinds.System"/> /
/// <see cref="WorkflowProvenanceActorKinds.Agent"/>.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowProvenanceActor(
    [property: Id(0)] string Kind,
    [property: Id(1)] string Id,
    [property: Id(2)] string? DisplayName = null);

/// <summary>
/// Immutable, machine-readable provenance fact for a key WorkflowRun
/// handoff (manual approval, retry, recovery/rerun, push, PR creation,
/// merge, CI passed, CI skipped). Recorded only from structured evidence:
/// control invocations with their initiating actor, or typed Action
/// outputs settled through the report path. Report free text
/// (<c>Detail</c>, messages, agent summaries) never produces a fact.
/// <para>
/// Run/issue lineage and the fact time travel on the CloudEvent envelope
/// (extensions + <c>time</c>); the payload carries the stage/attempt
/// position, the actor, the action, its outcome, and the reason/target
/// where applicable.
/// </para>
/// </summary>
public sealed record WorkflowProvenanceRecorded(
    string Action,
    string ActorKind,
    string ActorId,
    string Source,
    string Outcome,
    string? Stage = null,
    string? TaskId = null,
    int? Attempt = null,
    string? Result = null,
    string? Reason = null,
    string? Target = null,
    string? ActorDisplayName = null);
