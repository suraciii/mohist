using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans.Concurrency;

namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    /// <summary>Refreshes presence for control-plane heartbeat callers.</summary>
    Task HeartbeatAsync();
    /// <summary>Refreshes runner information and presence under the lifecycle gate.</summary>
    Task HeartbeatRepairAsync(RunnerInfo info);
    /// <summary>Atomically admits one poll round and captures its capacity.</summary>
    Task<RunnerPollAdmission> TryBeginPollAsync();
    /// <summary>
    /// Releases the matching poll round admitted by <see cref="TryBeginPollAsync"/>.
    /// A release from an older round cannot release a newer admission.
    /// </summary>
    Task EndPollAsync(Guid admissionToken);
    /// <summary>
    /// Records an ephemeral readiness observation for the current runner
    /// connection. The snapshot is only an admission fence; it never settles
    /// or replays work.
    /// </summary>
    Task<RunnerRuntimeReadinessSnapshot> ObserveRuntimeReadinessAsync(
        string? connectionGeneration,
        List<RuntimeReadinessWitness> witnesses);
    /// <summary>Atomically rejects new poll and work claims until cancelled.</summary>
    Task BeginDrainAsync();
    /// <summary>
    /// Atomically closes update admission and captures the active work that
    /// the caller must interrupt before restarting this runner. Returns null
    /// when the runner is not currently registered and online.
    /// </summary>
    Task<RunnerRuntimeState?> BeginUpdateInterruptAsync(string? updateInterruptId = null);
    /// <summary>
    /// Reopens admission only when the caller owns the currently persisted
    /// update-interrupt fence. A stale caller cannot cancel a later update.
    /// </summary>
    Task<RunnerUpdateInterruptCancelResult> CancelUpdateInterruptAsync(string updateInterruptId);
    /// <summary>Atomically reopens poll and work claim admission.</summary>
    Task CancelDrainAsync();
    /// <summary>
    /// Claims one workflow work item while checking the runner's live
    /// registration and capacity. Poll admission prevents overlapping polls;
    /// this operation is the authoritative availability boundary for fresh
    /// workflow claims.
    /// </summary>
    Task<WorkItem?> TryClaimWorkflowAsync(string workflowRunId, string? projectId, bool assignWorker);
    /// <summary>Claims one AgentJob from its owner ledger during a poll.</summary>
    Task<ClaimResult?> TryClaimAgentJobAsync(string agentJobId, string? projectId,
        CapabilityClaimExpectation? expectation = null);
    /// <summary>
    /// Marks the runner present. Poll and control-plane heartbeat both refresh
    /// presence; the former also participates in dispatch reconciliation.
    /// </summary>
    Task TouchPresenceAsync();

    [AlwaysInterleave]
    Task<RunnerRuntimeState> GetRuntimeStateAsync();
    Task UpdateBuildGitHashAsync(string? buildGitHash);
    Task UpdateRuntimeIdentityAsync(
        string? buildGitHash,
        string? component,
        string? version,
        string? sourceRevision,
        string? treeHash,
        string? artifactDigest,
        string? releaseId,
        long? generation,
        string? connectionGeneration = null);
    Task<RunnerInfo?> GetInfoAsync();

    /// <summary>
    /// Returns the current persisted dispatch capacity (slots). Sourced
    /// exclusively from the control-plane definition state — a value
    /// reported by the runner process via register/heartbeat SHALL NOT
    /// influence the returned value.
    /// </summary>
    [AlwaysInterleave]
    Task<int> GetSlotsAsync();

    /// <summary>
    /// Updates the persisted dispatch capacity (slots). Write-through:
    /// the value is persisted to the definition store before the in-memory
    /// cache is updated, so the next dispatch cycle honors the new value
    /// without requiring the runner process to re-register or restart.
    /// </summary>
    Task UpdateAsync(int slots);
}

public static class RunnerCapacity
{
    public const int DefaultMaxWorkflowSlots = 1;
}

[GenerateSerializer]
public sealed record ActionCatalog(
    [property: Id(0)] ActionCatalogEntry[] Actions,
    [property: Id(1)] ActionCatalogTombstone[] Tombstones);

[GenerateSerializer]
public sealed record ActionCatalogEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] ActionCatalogInput[] Inputs,
    [property: Id(2)] ActionCatalogOutput[] Outputs,
    [property: Id(3)] ActionCatalogError[] Errors,
    [property: Id(4)] string? Description = null,
    [property: Id(5)] string[]? Capabilities = null);

[GenerateSerializer]
public sealed record ActionCatalogInput(
    [property: Id(0)] string Name,
    [property: Id(1)] string[] Types,
    [property: Id(2)] bool Required,
    [property: Id(3)] JsonElement? Default = null,
    [property: Id(4)] string? Description = null);

[GenerateSerializer]
public sealed record ActionCatalogOutput(
    [property: Id(0)] string Name,
    [property: Id(1)] string? Description = null);

[GenerateSerializer]
public sealed record ActionCatalogError(
    [property: Id(0)] string Code,
    [property: Id(1)] string? Description = null);

[GenerateSerializer]
public sealed record ActionCatalogTombstone(
    [property: Id(0)] string Name,
    [property: Id(1)] string Guidance);

[GenerateSerializer]
public sealed record RuntimeCatalogEntry(
    [property: Id(0)] string[]? Models = null,
    [property: Id(1)] Dictionary<string, string[]>? Variants = null,
    [property: Id(2)] Dictionary<string, string[]>? ReasoningEfforts = null,
    [property: Id(3)] bool? SupportsReasoningEffort = null,
    [property: Id(4)] bool? Complete = null,
    [property: Id(5)] string? CapabilityRevision = null);

/// <summary>
/// Immutable evidence used at the Runner-to-owner claim boundary. The
/// execution tuple is kept generic; runtime adapters translate it only after
/// the owner has accepted the conditional claim.
/// </summary>
[GenerateSerializer]
public sealed record CapabilityClaimExpectation(
    [property: Id(0)] string OwnerKind,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] string WorkId,
    [property: Id(3)] string? Runtime,
    [property: Id(4)] string? Model,
    [property: Id(5)] string? ReasoningEffort,
    [property: Id(6)] string? Variant,
    [property: Id(7)] string? CapabilityRevision,
    [property: Id(8)] long? RuntimeGeneration,
    [property: Id(9)] string? ConnectionGeneration);

[GenerateSerializer]
public record RunnerInfo(
    string RunnerId,
    string[] Capabilities,
    string Hostname,
    string? ProjectId,
    string[]? CoderModels = null,
    string Kind = "external",
    DateTimeOffset? RegisteredAt = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null,
    ActionCatalog? ActionCatalog = null,
    Dictionary<string, RuntimeCatalogEntry>? RuntimeCatalogs = null,
    string? Component = null,
    string? Version = null,
    string? SourceRevision = null,
    string? TreeHash = null,
    string? ArtifactDigest = null,
    string? ReleaseId = null,
    long? Generation = null,
    string? ConnectionGeneration = null);

[GenerateSerializer]
public record WorkDispatch(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string WorkId,
    [property: Id(2)] string? Uses = null,
    [property: Id(3)] string? With = null,
    [property: Id(4)] string? Variables = null,
    [property: Id(5)] string WorkType = "task",
    [property: Id(6)] string? Stage = null,
    [property: Id(7)] string? Title = null,
    [property: Id(8)] WorkIssueRef? Issue = null,
    [property: Id(9)] string? Artifacts = null,
    [property: Id(11)] string OwnerKind = WorkDispatchOwnerKinds.Workflow,
    [property: Id(12)] string? AgentJobId = null,
    [property: Id(13)] string? SetVars = null,
    [property: Id(14)] string? Recovery = null,
    /// <summary>
    /// Project id for the dispatch envelope. For workflow dispatches the
    /// project is carried on <see cref="Issue"/>; for agent-job
    /// dispatches the grain sources it from the launch context. Null
    /// for workflow dispatches (the runner continues to read it from
    /// <c>Issue.ProjectId</c>). New field; older-field consumers
    /// ignore it.
    /// </summary>
    [property: Id(15)] string? ProjectId = null,
    /// <summary>
    /// AgentSession id for the dispatch envelope. Populated only for
    /// agent-job dispatches whose launch minted a generic
    /// (non-workflow) AgentSession; the runner uses it verbatim as the
    /// session identity for runtime events. Null for workflow
    /// dispatches. New field; older-field consumers ignore it.
    /// </summary>
    [property: Id(16)] string? AgentSessionId = null,
    [property: Id(17)] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RecoveryRemaining = null,
    [property: Id(18)] int? EpicNumber = null,
    /// <summary>
    /// Task-level completion contract (files, markers, failIf,
    /// <c>path: _output</c>) carried as an expanded JSON string for
    /// task-variant dispatches. The Workflow task executor reads and
    /// evaluates this after the Action returns; the Action itself does
    /// not see <c>expect</c>. Null for checks-variant dispatches and
    /// tasks without a completion contract.
    /// </summary>
    [property: Id(19)] string? Expect = null,
    /// <summary>
    /// Resolved Agent profile identity for AgentJob dispatches. Required
    /// for AgentJob ownership and absent on workflow dispatches.
    /// </summary>
    [property: Id(20)] string? AgentId = null,
    [property: Id(21)] AgentExecutionDefinition? AgentDefinition = null,
    /// <summary>
    /// Launch-time <c>SessionInput</c> id the coordinator durably
    /// recorded on the AgentSession before the AgentJob dispatched.
    /// The runner uses this to correlate its reports with the
    /// durable input and to skip emitting a duplicate <c>session.input</c>
    /// record for an AgentJob launch. Null for
    /// legacy dispatches that predate the idempotent launch path;
    /// the runner treats null as "publish the initial input as
    /// before".
    /// </summary>
    [property: Id(22)] string? InitialInputId = null,
    /// <summary>
    /// Launch-time <c>AgentTurn</c> id the coordinator durably
    /// recorded on the AgentSession. The runner correlates its
    /// executing/terminal progress with this id. Null for legacy
    /// dispatches.
    /// </summary>
    [property: Id(23)] string? InitialTurnId = null,
    [property: Id(24)] string? PinnedRunnerId = null,
    [property: Id(25)] AgentSessionStartup? AgentSessionStartup = null,
    /// <summary>
    /// Persisted Workflow task-attempt identity. Workflow Agent input delivery
    /// carries this together with <see cref="WorkId"/> so the Server can bind
    /// the Turn without trusting mutable Session labels.
    /// </summary>
    [property: Id(26)] string? TaskRunId = null,
    [property: Id(27)] CapabilityClaimExpectation? CapabilityClaim = null)
{
    public WorkDispatch() : this(string.Empty, string.Empty) { }
}

public static class WorkDispatchOwnerKinds
{
    public const string Workflow = "workflow";
    public const string AgentJob = "agent-job";
}

/// <summary>
/// The process's full level state, sent in every poll body. The DispatchService
/// reconciles <c>desired − reported</c> to redeliver lost dispatches and decide
/// new claims.
/// </summary>
[GenerateSerializer]
public sealed record RunnerPollRequest(
    [property: Id(0)] List<string> InFlight,
    [property: Id(1)] List<string> AwaitingAck,
    [property: Id(2)] List<RuntimeReadinessWitness>? RuntimeReadiness = null,
    [property: Id(3)] string? ConnectionId = null,
    [property: Id(4)] string? ConnectionGeneration = null,
    [property: Id(5)] bool? AdmissionReady = null)
{
    public RunnerPollRequest() : this([], []) { }
}

[GenerateSerializer]
public sealed record RuntimeReadinessWitness(
    [property: Id(0)] string Runtime,
    [property: Id(1)] bool Ready,
    [property: Id(2)] long? Generation = null);

[GenerateSerializer]
public sealed record RunnerRuntimeReadinessSnapshot(
    [property: Id(0)] string? ConnectionGeneration,
    [property: Id(1)] List<RuntimeReadinessWitness> Witnesses)
{
    public static RunnerRuntimeReadinessSnapshot Empty { get; } = new(null, []);

    public bool Allows(IReadOnlyList<string>? requiredRuntimes)
    {
        if (requiredRuntimes is null)
            return false;
        if (requiredRuntimes.Count == 0)
            return true;

        if (Witnesses.Count == 0)
            return ConnectionGeneration is null;

        return requiredRuntimes.All(runtime => Witnesses.Any(witness =>
            witness.Ready
            && witness.Generation is > 0
            && string.Equals(witness.Runtime, runtime, StringComparison.OrdinalIgnoreCase)));
    }
}

[GenerateSerializer]
public sealed record RunnerPollAdmission(
    [property: Id(0)] bool Admitted,
    /// <summary>
    /// Capacity observed when the poll starts. It is informational only;
    /// each fresh workflow claim rechecks live capacity.
    /// </summary>
    [property: Id(1)] int Slots,
    /// <summary>
    /// Opaque token for the admitted poll. Rejected admissions use
    /// <see cref="Guid.Empty"/>.
    /// </summary>
    [property: Id(2)] Guid AdmissionToken = default);

/// <summary>
/// The dispatches rendered for this poll: redeliveries (desired − reported) plus
/// new claims against spare capacity. Multiple dispatches per poll replace the
/// old one-dispatch-per-poll limit.
/// </summary>
[GenerateSerializer]
public sealed record RunnerPollResponse(
    [property: Id(0)] List<WorkDispatch> Dispatches)
{
    public RunnerPollResponse() : this([]) { }
}

[GenerateSerializer]
public record WorkResult(
    string Status,
    string? Message = null,
    System.Text.Json.JsonElement? Output = null,
    int? ExitCode = null,
    string[]? ArtifactUploadIds = null,
    [property: Id(5)] List<RuntimeTaskInput>? AddTasks = null,
    [property: Id(6)] ExecutionError? Error = null)
{
    /// <summary>
    /// Flattened <c>Error.Code</c> for cross-domain readers:
    /// AgentJobGrain projects the runner failure category
    /// without depending on the Workflow domain's
    /// <c>ExecutionError</c> type. Returns <c>null</c> when the
    /// dispatcher payload omitted the <c>Error</c> block.
    /// </summary>
    public string? ErrorCode => Error?.Code;
}

public enum RunnerStatus { Online, Offline }

[GenerateSerializer]
public record RunnerRuntimeState(
    RunnerStatus Status,
    DateTimeOffset LastHeartbeatAt,
    IReadOnlyList<RunnerActiveWorkItem> ActiveWorks,
    bool Draining = false,
    string? UpdateInterruptId = null);

[GenerateSerializer]
public enum RunnerUpdateInterruptCancelStatus
{
    Cancelled,
    AlreadyCancelled,
    Superseded,
}

[GenerateSerializer]
public sealed record RunnerUpdateInterruptCancelResult(
    [property: Id(0)] string UpdateInterruptId,
    [property: Id(1)] RunnerUpdateInterruptCancelStatus Status);

[GenerateSerializer]
public sealed record RunnerActiveWorkItem(
    [property: Id(0)] string WorkId,
    [property: Id(1)] string OwnerKind,
    [property: Id(2)] string OwnerId,
    [property: Id(3)] string WorkType,
    [property: Id(4)] string? Stage,
    [property: Id(5)] string? Title,
    [property: Id(6)] WorkIssueRef? Issue = null,
    [property: Id(7)] DateTimeOffset? TakenAt = null);
