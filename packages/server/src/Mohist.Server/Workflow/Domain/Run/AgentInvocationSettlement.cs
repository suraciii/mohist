using System.Text.Json;
using Orleans;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Terminal status of the AgentJob backing a workflow Agent invocation,
/// carried over the typed workflow-terminal transport. The AgentJob owns
/// this verdict; the Workflow finalizer owns the task decision derived
/// from it.
/// </summary>
public enum AgentInvocationTerminalStatus
{
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Decision facts of the completion evaluation the execution boundary
/// (the runner's agent-job executor) computed against the frozen
/// task-level <c>expect</c> after the agent turn settled. The evaluation
/// rides the terminal transport typed; the finalizer owns the decision
/// (an unsatisfied evaluation fails the Workflow task with the inline
/// <c>expectation-failed</c> code and this message while the AgentJob
/// terminal verdict stays <c>completed</c>).
/// </summary>
[GenerateSerializer]
public sealed record AgentInvocationExpectation(
    [property: Id(0)] bool Satisfied,
    [property: Id(1)] string? Matched,
    [property: Id(2)] string? Message);

/// <summary>
/// Typed AgentJob terminal delivered to the Workflow-owned finalizer
/// (issue 559, design D5/D7). Carries the invocation identity, the
/// terminal facts, and the boundary completion evaluation under the
/// stable delivery id (<c>workflow-terminal:{jobKey}</c>) so duplicate
/// or redelivered terminals resolve against the same identity. The
/// full typed payload is also what the settlement receipt freezes.
/// </summary>
[GenerateSerializer]
public sealed record AgentInvocationTerminal(
    [property: Id(0)] string DeliveryId,
    [property: Id(1)] string InvocationId,
    [property: Id(2)] string ProjectId,
    [property: Id(3)] string WorkflowRunId,
    [property: Id(4)] string TaskRunId,
    [property: Id(5)] string WorkId,
    [property: Id(6)] string JobId,
    [property: Id(7)] string? SessionId,
    [property: Id(8)] AgentInvocationTerminalStatus Status,
    [property: Id(9)] string? Message,
    [property: Id(10)] string? FailureReason,
    [property: Id(11)] string? FailureCategory,
    [property: Id(12)] int? ExitCode,
    [property: Id(13)] string[]? ArtifactUploadIds,
    [property: Id(14)] AgentInvocationExpectation? Expectation,
    [property: Id(15)] DateTimeOffset RecordedAt,
    [property: Id(16)] JsonElement? Output = null,
    [property: Id(17)] string? InputId = null,
    [property: Id(18)] string? TurnId = null);

/// <summary>
/// Immutable invocation linkage persisted once on the
/// <see cref="TaskRun"/> at handoff time (design D9): the invocation id
/// plus the minted AgentJob/AgentSession/SessionInput/AgentTurn
/// identifiers. The Workflow run is the queryable source for the
/// Workflow surface; the AgentSession carries the reciprocal lineage as
/// metadata labels. The linkage is the stop cascade's handle to the
/// backing AgentJob.
/// </summary>
[GenerateSerializer]
public sealed record AgentInvocationLink(
    [property: Id(0)] string InvocationId,
    [property: Id(1)] string TaskRunId,
    [property: Id(2)] string WorkId,
    [property: Id(3)] string JobId,
    [property: Id(4)] string SessionId,
    [property: Id(5)] string InputId,
    [property: Id(6)] string TurnId);

/// <summary>
/// Durable per-effect completion receipt the Workflow-owned finalizer
/// guards settlement with (design D7). The receipt freezes the terminal
/// snapshot so an interrupted settlement resumes without re-reading the
/// AgentJob, and records one applied flag per effect in the inline
/// executor's order: artifact binding, setVars extraction/application,
/// then the task outcome (settlement, which includes advancement).
/// Effects not applicable to a terminal (uploads, setVars on failure)
/// are marked applied trivially so the resume logic stays uniform.
/// </summary>
public sealed class AgentInvocationSettlement
{
    /// <summary>The frozen terminal delivery this receipt settles.</summary>
    public required AgentInvocationTerminal Terminal { get; init; }

    /// <summary>Upload ids were bound (or the bind failure was frozen).</summary>
    public bool ArtifactsBound { get; set; }

    /// <summary>Bound artifact paths (or the bind error) frozen at bind time.</summary>
    public string[]? BoundArtifactPaths { get; set; }

    /// <summary>Artifact bind failure; a successful task report cannot be built when set.</summary>
    public string? ArtifactBindError { get; set; }

    /// <summary>setVars were extracted and applied (or the task cannot apply them).</summary>
    public bool SetVarsApplied { get; set; }

    /// <summary>setVars extraction/patch failure frozen at application time.</summary>
    public string? SetVarsFailure { get; set; }

    /// <summary>The task outcome was applied through the domain settlement calls.</summary>
    public bool OutcomeApplied { get; set; }

    /// <summary>The task settlement portion of the outcome was applied.</summary>
    public bool SettlementApplied { get; set; }

    /// <summary>Workflow advancement was applied with the task settlement.</summary>
    public bool AdvancementApplied { get; set; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? SettledAt { get; set; }

    // Outcome and advancement are committed by one domain call, but remain
    // separate durable facts so the receipt records every completion effect.
    // OutcomeApplied is retained for additive compatibility with the first
    // receipt shape and is set together with the two explicit flags.
    public bool IsSettled => ArtifactsBound
        && SetVarsApplied
        && (SettlementApplied || OutcomeApplied)
        && (AdvancementApplied || OutcomeApplied);
}

/// <summary>Ack for a terminal delivery to the finalizer.</summary>
[GenerateSerializer]
public enum AgentInvocationSettlementAck
{
    /// <summary>The terminal's effects were applied (or resumed to completion).</summary>
    Applied,

    /// <summary>The delivery was already applied from the receipt; nothing reapplied.</summary>
    AlreadyApplied,

    /// <summary>The attempt is no longer reportable (task terminal, run
    /// stopped or deleted, unknown attempt); acknowledged without effects.</summary>
    Stale,
}

public static class AgentInvocationSettlementExtensions
{
    private const string PromisePattern = @"^<promise>\s*([^<>\s]+)\s*</promise>$";

    /// <summary>
    /// Extracts the bare verdict from a matched
    /// <c>&lt;promise&gt;VALUE&lt;/promise&gt;</c> marker, mirroring the
    /// runner's <c>promiseValue</c> so a handoff task projects exactly the
    /// output the inline executor produces (<c>{"promise": "VALUE"}</c>).
    /// A non-promise match (a file-backed marker) projects no output,
    /// matching the inline agent-turn projection.
    /// </summary>
    public static string? PromiseValue(string? matched)
    {
        if (string.IsNullOrEmpty(matched)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(matched, PromisePattern);
        return match.Success ? match.Groups[1].Value : null;
    }
}
