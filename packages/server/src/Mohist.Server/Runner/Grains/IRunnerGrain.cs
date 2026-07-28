using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans.Concurrency;

namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    /// <summary>Legacy heartbeat with no info payload. Does not refresh presence.</summary>
    Task HeartbeatAsync();
    /// <summary>Refreshes runner information. Does not refresh presence.</summary>
    Task HeartbeatRepairAsync(RunnerInfo info);
    /// <summary>
    /// Agent-job assignment stays push-based because the job grain owns the
    /// dispatch snapshot. Poll delivery reconciles that stable work against the
    /// runner's process-lifetime reported set.
    /// </summary>
    [AlwaysInterleave]
    Task<RunnerWorkAssignmentResult> AssignAgentJobAsync(WorkDispatch work);
    Task<RunnerWorkReportResult> ReportAgentJobResultAsync(string agentJobId, string workId, WorkResult result);
    /// <summary>Atomically admits one reconciliation round and captures its capacity.</summary>
    Task<RunnerPollAdmission> TryBeginPollAsync();
    /// <summary>Releases the reconciliation round admitted by <see cref="TryBeginPollAsync"/>.</summary>
    Task EndPollAsync();
    /// <summary>
    /// Claims one workflow work item while checking the runner's live
    /// registration and capacity. Poll admission prevents overlapping polls;
    /// this operation is the authoritative availability boundary for fresh
    /// workflow claims.
    /// </summary>
    Task<WorkItem?> TryClaimWorkflowAsync(string workflowRunId, string? projectId, bool assignWorker);
    /// <summary>Returns active Agent capacity and at most one missing stable dispatch.</summary>
    Task<AgentJobPollState> ReconcileAgentJobsAsync(List<string> reportedWorkKeys);
    /// <summary>
    /// Marks the runner present. Poll IS the heartbeat under the
    /// reconciliation model: the
    /// DispatchService calls this on every poll; the HTTP heartbeat degrades
    /// to an info-refresh channel.
    /// </summary>
    Task TouchPresenceAsync();

    [AlwaysInterleave]
    Task<RunnerRuntimeState> GetRuntimeStateAsync();
    Task UpdateBuildGitHashAsync(string? buildGitHash);
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
    [property: Id(4)] string? Description = null);

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
    [property: Id(1)] Dictionary<string, string[]>? Variants = null);

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
    Dictionary<string, RuntimeCatalogEntry>? RuntimeCatalogs = null);

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
    [property: Id(20)] string? AgentId = null)
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
    [property: Id(1)] List<string> AwaitingAck)
{
    public RunnerPollRequest() : this([], []) { }
}

[GenerateSerializer]
public sealed record RunnerPollAdmission(
    [property: Id(0)] bool Admitted,
    /// <summary>
    /// Capacity observed when the poll starts. It is informational only;
    /// each fresh workflow claim rechecks live capacity.
    /// </summary>
    [property: Id(1)] int Slots);

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
public sealed record AgentJobPollState(
    [property: Id(0)] int ActiveCount,
    [property: Id(1)] WorkDispatch? Dispatch);

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

[GenerateSerializer]
public sealed record RunnerWorkAssignmentResult(
    [property: Id(0)] RunnerWorkAssignmentStatus Status,
    [property: Id(1)] string? Reason = null);

public enum RunnerWorkAssignmentStatus
{
    Assigned,
    Rejected
}

[GenerateSerializer]
public sealed record RunnerWorkReportResult(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string? WorkflowStatus,
    [property: Id(2)] bool Tracked,
    [property: Id(3)] string? Reason = null,
    [property: Id(4)] string? OwnerKind = null,
    [property: Id(5)] string? OwnerId = null);

public enum RunnerStatus { Online, Offline }

[GenerateSerializer]
public record RunnerRuntimeState(
    RunnerStatus Status,
    DateTimeOffset LastHeartbeatAt,
    IReadOnlyList<RunnerActiveWorkItem> ActiveWorks);

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
