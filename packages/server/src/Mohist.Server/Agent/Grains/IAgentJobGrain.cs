using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public interface IAgentJobGrain : IGrainWithStringKey, IRemindable
{
    Task<bool> IsWorkRunnableAsync(string runnerId, string workId);
    Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result);
    Task<AgentJobStatus> GetStatusAsync();
    Task<AgentJobCancelResult> CancelAsync() =>
        Task.FromResult(new AgentJobCancelResult(AgentJobCancelDisposition.AlreadyEnded, AgentJobStatus.Unknown));
    Task<string?> GetCurrentWorkIdAsync();
    Task AssignRunnerAsync(string runnerId, string workId);
    Task<bool> RecordRuntimeSessionBindingAsync(string runnerId, string workId, string sessionId, string runtimeSessionId) =>
        Task.FromResult(false);
    Task SubmitAsync(AgentJobInput input);
    Task EnsureSubmittedAsync(AgentJobInput input);
    Task CheckTimeoutsAsync();
    Task<AgentJobTerminalResult> GetTerminalResultAsync();
    Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync();

    /// <summary>
    /// Idempotent routed-launch preparation entry point. The caller
    /// passes the fully-resolved
    /// <see cref="RoutedAgentLaunchPlan"/> it just computed from the
    /// event lineage; the grain registers a durable recovery reminder,
    /// persists the canonical plan (or its preflight-failed equivalent),
    /// and returns the canonical plan so redelivery observes the same
    /// outcome as the first delivery. Subsequent calls are no-ops: the
    /// caller may continue opening the AgentSession and submitting the
    /// AgentJobInput from the returned canonical plan only.
    /// </summary>
    Task<RoutedAgentLaunchPlan> EnsurePreparedAsync(RoutedAgentLaunchPlan plan);

    /// <summary>
    /// Advance the persisted prepared launch: open the AgentSession
    /// from the canonical plan (idempotent), persist launch-ready, and
    /// either submit the executable AgentJob to dispatch or enter the
    /// durable preflight terminal-delivery protocol. Called immediately
    /// after <see cref="EnsurePreparedAsync"/>, on activation recovery,
    /// and from the recovery reminder tick.
    /// </summary>
    Task AdvancePreparedLaunchAsync();

    /// <summary>
    /// Force the job to a failed terminal state. Used by the runner-side
    /// control plane when a work is synthesized as failed (timeout or
    /// runner-loss) but the normal <see cref="ReportResultAsync"/> channel
    /// cannot accept the report (e.g., the grain reactivated in Pending
    /// status and no longer recognises the runner/work pair).
    /// </summary>
    Task FailAsync(string reason, string? agentId = null);

    /// <summary>
    /// Idempotent manual-launch preparation entry point. The coordinator
    /// passes the resolved launch snapshot
    /// (prompt, agent id, runtime, agent session id, generated input
    /// and turn ids) the first time it converges; the grain stores
    /// the snapshot as <c>ManualPlan</c> and returns the canonical
    /// record on every subsequent call. The grain does not dispatch
    /// until <see cref="SubmitPreparedLaunchAsync"/> is called.
    /// </summary>
    Task<AgentJobInput> PrepareManualLaunchAsync(PrepareManualLaunchCommand command) =>
        Task.FromResult(new AgentJobInput(command.Prompt, AgentId: command.AgentId, AgentSessionId: command.SessionId, InitialInputId: command.InputId, InitialTurnId: command.TurnId));

    /// <summary>
    /// Submit a previously-prepared manual launch to dispatch. The
    /// grain is idempotent: a re-submit with the same input is a
    /// no-op that bumps dispatch attempts if needed. The grain
    /// refuses to submit when the launch record is missing or
    /// belongs to a different plan.
    /// </summary>
    Task SubmitPreparedLaunchAsync() => Task.CompletedTask;

    /// <summary>
    /// Move a non-terminal AgentJob to <see cref="AgentJobStatus.Unknown"/>
    /// Used when a Runner disconnect, a status
    /// timeout, or any inconclusive delivery leaves the original
    /// first execution unverifiable. The grain preserves the durable
    /// Job/work/input/turn identities; an authoritative running or
    /// terminal report later resolves the original Job rather than
    /// minting a replacement. Idempotent: re-issuing with the same
    /// reason is a no-op while the Job remains Unknown; a terminal
    /// Job or a Job already moving to Unknown with a different
    /// reason is left untouched so the existing transition path is
    /// never overwritten.
    /// </summary>
    Task MarkUnknownAsync(string reason) => Task.CompletedTask;

    Task ReconcileRunningAsync(string runnerId, string workId) => Task.CompletedTask;
    Task ConcurrencyPermitGrantedAsync() => Task.CompletedTask;
}

[GenerateSerializer]
public sealed record PrepareManualLaunchCommand(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string InputId,
    [property: Id(2)] string TurnId,
    [property: Id(3)] string Prompt,
    [property: Id(4)] string? Model = null,
    [property: Id(5)] string? WorkspacePath = null,
    [property: Id(6)] string? ProjectId = null,
    [property: Id(7)] string? Runtime = null,
    [property: Id(8)] string? AgentId = null,
    [property: Id(9)] string? AgentInstructions = null,
    [property: Id(10)] System.Text.Json.JsonElement? AgentConfig = null,
    [property: Id(11)] string? Variant = null,
    [property: Id(12)] int? IssueNumber = null,
    [property: Id(13)] int? EpicNumber = null,
    [property: Id(14)] string? WorkflowRunId = null,
    [property: Id(15)] ConnectionLaunchOrigin? ConnectionOrigin = null);

[GenerateSerializer]
public sealed record PendingTerminalDeliveryEvent(
    [property: Id(0)] string EventId,
    [property: Id(1)] ConnectionLaunchOrigin Origin,
    [property: Id(2)] AgentJobStatus Status,
    [property: Id(3)] string? Message,
    [property: Id(5)] string? FailureReason,
    [property: Id(6)] string? FailureCategory,
    [property: Id(7)] int ArtifactCount,
    [property: Id(8)] int? ExitCode,
    [property: Id(9)] DateTimeOffset RecordedAt);

[GenerateSerializer]
public sealed record AgentJobReportResult(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] string? Reason = null);

[GenerateSerializer]
public sealed record AgentJobRuntimeSnapshot(
    [property: Id(0)] AgentJobStatus Status,
    [property: Id(1)] string? RunnerId,
    [property: Id(2)] string? CurrentWorkId,
    [property: Id(3)] string? FailureReason,
    [property: Id(4)] int DispatchAttempts = 0,
    [property: Id(5)] bool RunnerAccepted = false,
    [property: Id(6)] bool HasPendingSessionClose = false,
    [property: Id(7)] string? ProjectId = null,
    [property: Id(8)] AgentExecutionDefinition? ExecutionDefinition = null,
    /// <summary>
    /// Linked AgentSession id captured at launch time. Surface so the
    /// composite observation assembler can
    /// resolve the Session without re-reading the AgentJob input
    /// snapshot. Null for legacy jobs that pre-date the manual
    /// coordinator path.
    /// </summary>
    [property: Id(9)] string? AgentSessionId = null,
    /// <summary>
    /// Launch-time <c>SessionInput</c> id the coordinator durably
    /// recorded on the AgentSession. Surface so the observation
    /// assembler can correlate the durable identities without
    /// round-tripping through the AgentSession.
    /// </summary>
    [property: Id(10)] string? InitialInputId = null,
    /// <summary>
    /// Launch-time <c>AgentTurn</c> id the coordinator durably
    /// recorded on the AgentSession.
    /// </summary>
    [property: Id(11)] string? InitialTurnId = null);

/// <summary>
/// Durable payload persisted on the AgentJob grain for a pending
/// terminal close delivery to the owning AgentSession. The AgentJob
/// keeps this record until the AgentSession has synchronously persisted
/// the idempotently identified terminal <c>session.activity</c> transcript fact
/// (see design decision 2). <see cref="DeliveryId"/> is the stable
/// correlation key the AgentSession stores on the close event and uses
/// to deduplicate retried deliveries; the format is
/// <c>agent-job:{jobKey}:terminal</c> so retries always land on the
/// same close record.
/// </summary>
[GenerateSerializer]
public sealed record PendingSessionClose(
    [property: Id(0)] string DeliveryId,
    [property: Id(1)] string Status,
    [property: Id(2)] int? ExitCode,
    [property: Id(3)] string? FailureReason,
    [property: Id(4)] string? FailureCategory,
    [property: Id(5)] DateTimeOffset RecordedAt);

/// <summary>
/// Durable payload persisted on the AgentJob grain for a pending
/// failed-terminal CloudEvent append. The AgentJob writes a
/// <c>com.mohist.agent.job.failed</c> event exactly once for every
/// failed terminal transition; until the append succeeds the grain
/// keeps this record and the <c>agent-job-recovery</c> reminder keeps
/// retrying. <see cref="EventId"/> is the stable CloudEvent id
/// (<c>agent-job:{jobKey}:failed</c>) so retried appends collapse to
/// the same envelope via the store-level (source, eventId) dedup.
/// </summary>
[GenerateSerializer]
public sealed record PendingFailureEvent(
    [property: Id(0)] string EventId,
    [property: Id(1)] string? FailureReason,
    [property: Id(2)] string? FailureCategory,
    [property: Id(3)] DateTimeOffset RecordedAt);

public static class AgentJobSessionDeliveryIds
{
    public static string TerminalDeliveryId(string jobKey) =>
        $"agent-job:{jobKey}:terminal";

    public static string FailureEventId(string jobKey) =>
        $"agent-job:{jobKey}:failed";

    public static string TerminalDeliveryEventId(string jobKey) =>
        $"agent-job:{jobKey}:terminal-delivery";
}

public static class AgentJobFailureReasons
{
    public const string RunnerUnavailable = "runner-unavailable";
    public const string ReportTimeout = "report-timeout";

    /// <summary>
    /// Stable reason code for routed-launch preflight failures where
    /// no AgentJob dispatch should be issued. Surfaced verbatim on the
    /// AgentSession terminal
    /// close payload and on the issue event feed.
    /// </summary>
    public const string WorkspaceUnavailable = "workspace-unavailable";
}

public enum AgentJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    /// <summary>
    /// Nonterminal, non-dispatchable state. The
    /// AgentJob grain could not confirm whether the original work
    /// accepted or completed the prompt; the durable identities (job,
    /// work, input, turn) are preserved for reconciliation but the
    /// job MUST NOT auto-replay or synthesise a failed/completed
    /// verdict. Resolves only on authoritative running or terminal
    /// Runner evidence.
    /// </summary>
    Unknown,
}

[GenerateSerializer]
public sealed record AgentJobCancelResult(
    [property: Id(0)] AgentJobCancelDisposition Disposition,
    [property: Id(1)] AgentJobStatus Status);

public enum AgentJobCancelDisposition
{
    Cancelled,
    AlreadyEnded,
    Executing,
}

/// <summary>
/// Canonical routed-launch preparation plan produced by the
/// <see cref="RoutedAgentLaunchContextResolver"/> after a routing rule
/// has matched. The AgentJob grain persists this plan (or its
/// preflight-failed equivalent) before any Session open or dispatch
/// attempt; redelivery always reads back the same canonical plan, so a
/// later delivery cannot overwrite the first delivery's resolved
/// workspace or lineage.
///
/// <para>
/// <see cref="SessionId"/> is the stable AgentSession id minted from the
/// <c>projectId/eventId/ruleId</c> trigger identity; <see cref="JobKey"/>
/// is the matching AgentJob grain key. The AgentLauncher carries the
/// trigger identity forward into the AgentSession labels so the
/// session is queryable by event/rule; the grain owns only the
/// canonical plan.
/// </para>
///
/// <para>
/// The plan carries the resolved AgentJob input (model, variant,
/// instructions, agent id, agent name, agent config JSON, prompt,
/// runtime, skills) so recovery after process loss does not re-read
/// mutable Agent definitions. The agent config is stored as a raw JSON
/// string so Orleans' serializer keeps the canonical bytes verbatim.
/// </para>
///
/// <para>
/// <see cref="Runtime"/> is the resolved execution backend. It is
/// captured here so editing the Agent's backend
/// config after launch cannot change the in-flight execution; recovery
/// reuses the snapshotted runtime rather than re-reading mutable
/// Agent config. Append-only Orleans field id (next free after Prompt).
/// </para>
///
/// <para>
/// <see cref="Skills"/> is the resolved ordered Skills snapshot
/// captured at launch time so the Runner can resolve SKILL.md bodies
/// from its configured roots without re-reading mutable Agent state.
/// Absent on persisted plans written before this snapshot was added —
/// the runner treats an absent list as empty. Append-only Orleans
/// field id (next free after Runtime).
/// </para>
///
/// <para>
/// <see cref="WorkflowRunId"/> captures the explicit workflow-run id
/// from the launching CloudEvent when one is present (or the
/// issue-bound nonterminal run when the event is issue-scoped). It
/// stamps the durable failure-event envelope so the issue-grade
/// <c>com.mohist.agent.job.failed</c> event carries the same
/// workflow-run lineage as the routed launch source. Append-only
/// Orleans field id (next free after Skills).
/// </para>
/// </summary>
[GenerateSerializer]
public sealed record RoutedAgentLaunchPlan(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string EventId,
    [property: Id(2)] string RuleId,
    [property: Id(3)] string SessionId,
    [property: Id(4)] string JobKey,
    [property: Id(5)] int? IssueNumber,
    [property: Id(6)] int? EpicNumber,
    [property: Id(7)] string? WorkspacePath,
    [property: Id(8)] RoutedLaunchDisposition Disposition,
    [property: Id(9)] string? PreflightReason = null,
    [property: Id(10)] string? PreflightCategory = null,
    [property: Id(11)] DateTimeOffset PreparedAt = default,
    [property: Id(12)] string? AgentId = null,
    [property: Id(13)] string? AgentName = null,
    [property: Id(14)] string? AgentInstructions = null,
    [property: Id(15)] string? AgentConfigJson = null,
    [property: Id(16)] string? Model = null,
    [property: Id(17)] string? Variant = null,
    [property: Id(18)] string? Prompt = null,
    [property: Id(19)] string? Runtime = null,
    [property: Id(20)] IReadOnlyList<string>? Skills = null,
    [property: Id(21)] string? WorkflowRunId = null);

/// <summary>
/// Whether the canonical routed-launch plan is executable or already
/// decided as preflight-failed. The
/// AgentJob grain refuses to dispatch <see cref="PreflightFailed"/>.
/// </summary>
public enum RoutedLaunchDisposition
{
    Executable,
    PreflightFailed,
}

/// <summary>
/// Input snapshot for a standalone AgentJob. When the launch resolves a
/// project-scoped <c>Agent</c> profile, the resolved snapshot (id,
/// instructions, model, and variant) is captured here so the executed
/// bytes are stable for the lifetime of the job — even if the Agent is
/// edited concurrently. Raw-prompt-only AgentJobs are rejected before
/// dispatch because a failed job must retain a real Agent identity.
/// </summary>
[GenerateSerializer]
public sealed record AgentJobInput(
    [property: Id(0)] string Prompt,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkspacePath = null,
    [property: Id(3)] string? ProjectId = null,
    /// <summary>
    /// Resolved execution backend snapshot captured at launch time.
    /// Resolved launches pin the runtime (defaulting to
    /// <c>AgentConfigSchema.OpenCodeRuntime</c>) so the runner
    /// executor can pick the right runtime and recovery reuses the
    /// snapshotted value rather than re-reading mutable Agent config.
    /// Append-only Orleans field id (next free after
    /// <see cref="ProjectId"/>).
    /// </summary>
    [property: Id(4)] string? Runtime = null,
    /// <summary>
    /// Resolved Agent profile identity captured at launch time. Required
    /// for every executable AgentJob and carried through to dispatch for
    /// traceability.
    /// </summary>
    [property: Id(5)] string? AgentId = null,
    /// <summary>
    /// Resolved Agent <c>Instructions</c> snapshot captured at launch
    /// time. The AgentJob executor composes this with the caller
    /// prompt and emits the single composed execution input. Null
    /// when no Agent definition was supplied.
    /// </summary>
    [property: Id(6)] string? AgentInstructions = null,
    /// <summary>
    /// Resolved Agent <c>AgentConfig</c> snapshot captured at launch
    /// time. Carried for audit/traceability; the AgentJob executor
    /// projects this into a flat Agent-owned payload. Null when no
    /// Agent definition was supplied or the Agent has no config.
    /// </summary>
    [property: Id(7)] JsonElement? AgentConfig = null,
    /// <summary>
    /// Minted AgentSession id used by the runner to record runtime
    /// events against a generic (non-workflow) AgentSession. Optional;
    /// when null the dispatch envelope leaves <c>AgentSessionId</c>
    /// unset (workflow-shaped path).
    /// </summary>
    [property: Id(8)] string? AgentSessionId = null,
    /// <summary>
    /// Resolved Agent <c>variant</c> snapshot captured at launch time.
    /// Surfaced in the dispatch envelope so the runner can apply the
    /// launch-time variant to the runtime turn. Null when no Agent
    /// definition supplied a variant or the launch did not pin one.
    /// </summary>
    [property: Id(9)] string? Variant = null,
    /// <summary>
    /// Issue number captured at launch time. Populated by the launch
    /// context (manual HTTP, mention, routed, or preflight) so the
    /// durable failure-event envelope can stamp the issue lineage
    /// without re-reading mutable Issue state. Append-only Orleans
    /// field id (next free after Variant).
    /// </summary>
    [property: Id(10)] int? IssueNumber = null,
    /// <summary>
    /// Epic number captured at launch time. Populated by the launch
    /// context so the durable failure-event envelope can stamp the
    /// epic lineage. Append-only Orleans field id (next free after
    /// IssueNumber).
    /// </summary>
    [property: Id(11)] int? EpicNumber = null,
    /// <summary>
    /// Workflow-run id captured at launch time. Populated by the
    /// launch context (routed launch by the routed-resolver, mention
    /// launch by the comment-attached run, manual launch by the
    /// caller-supplied execution context) so the durable
    /// failure-event envelope can stamp the workflow-run lineage.
    /// Append-only Orleans field id (next free after EpicNumber).
    /// </summary>
    [property: Id(12)] string? WorkflowRunId = null,
    /// <summary>
    /// Ordered Skills captured at launch time from the resolved Agent
    /// definition. Persisted on the durable AgentJob so the dispatch
    /// envelope can deliver the configured Skill names to the Runner
    /// for resolution against its configured Skill roots. Absent on
    /// records written before this snapshot was added — both the
    /// launcher and the dispatch builder treat an absent list as
    /// empty (no Skill input reaches the selected Runtime). Append-only
    /// Orleans field id (next free after WorkflowRunId).
    /// </summary>
    [property: Id(13)] IReadOnlyList<string>? Skills = null,
    /// Stable id of the launch-time <c>SessionInput</c> the
    /// coordinator durably recorded on the AgentSession before the
    /// AgentJob dispatched. The runner uses this to correlate its
    /// own reports with the durable input identity and to skip
    /// emitting a duplicate <c>session.input</c> record for an
    /// AgentJob launch. Append-only
    /// Orleans field id (next free after WorkflowRunId).
    /// </summary>
    [property: Id(14)] string? InitialInputId = null,
    /// <summary>
    /// Stable id of the launch-time <c>AgentTurn</c> the coordinator
    /// recorded on the AgentSession. The Runner correlates its
    /// executing/terminal progress with this id so the Session's
    /// turn status stays consistent with the Job's lifecycle.
    /// Append-only Orleans field id (next free after InitialInputId).
    /// </summary>
    [property: Id(15)] string? InitialTurnId = null);

[GenerateSerializer]
public sealed record AgentJobTerminalResult(
    [property: Id(0)] AgentJobStatus Status,
    [property: Id(1)] string? Message,
    [property: Id(2)] string? Output,
    [property: Id(3)] string[]? ArtifactUploadIds,
    [property: Id(4)] string? FailureReason,
    [property: Id(5)] int? ExitCode);
