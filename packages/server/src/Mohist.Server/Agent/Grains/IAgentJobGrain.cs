using System.Text.Json;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public interface IAgentJobGrain : IGrainWithStringKey, IRemindable
{
    Task<bool> IsWorkRunnableAsync(string runnerId, string workId);
    Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result);
    Task<AgentJobStatus> GetStatusAsync();
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
    /// Idempotent routed-launch preparation entry point (issue-449
    /// design decisions 1-3). The caller passes the fully-resolved
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
    Task FailAsync(string reason);
}

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
    [property: Id(6)] bool HasPendingSessionClose = false);

/// <summary>
/// Durable payload persisted on the AgentJob grain for a pending
/// terminal close delivery to the owning AgentSession. The AgentJob
/// keeps this record until the AgentSession has synchronously persisted
/// the idempotently identified <c>session.closed</c> transcript fact
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

public static class AgentJobSessionDeliveryIds
{
    public static string TerminalDeliveryId(string jobKey) =>
        $"agent-job:{jobKey}:terminal";
}

public static class AgentJobFailureReasons
{
    public const string RunnerUnavailable = "runner-unavailable";
    public const string ReportTimeout = "report-timeout";

    /// <summary>
    /// Stable reason code for routed-launch preflight failures where
    /// no AgentJob dispatch should be issued (issue-449 design
    /// decision 2). Surfaced verbatim on the AgentSession terminal
    /// close payload and on the issue event feed.
    /// </summary>
    public const string WorkspaceUnavailable = "workspace-unavailable";
}

public enum AgentJobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

/// <summary>
/// Canonical routed-launch preparation plan produced by the
/// <see cref="RoutedAgentLaunchContextResolver"/> after a routing rule
/// has matched. The AgentJob grain persists this plan (or its
/// preflight-failed equivalent) before any Session open or dispatch
/// attempt; redelivery always reads back the same canonical plan, so a
/// later delivery cannot overwrite the first delivery's resolved
/// workspace or lineage (issue-449 design decisions 1-3).
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
/// instructions, agent id, agent name, agent config JSON, prompt) so
/// recovery after process loss does not re-read mutable Agent
/// definitions. The agent config is stored as a raw JSON string so
/// Orleans' serializer keeps the canonical bytes verbatim.
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
    [property: Id(18)] string? Prompt = null);

/// <summary>
/// Whether the canonical routed-launch plan is executable or already
/// decided as preflight-failed (issue-449 design decision 2). The
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
/// edited concurrently. A raw-prompt-only AgentJob remains supported:
/// when <see cref="AgentId"/> is null, the runner receives the bare
/// <see cref="Prompt"/>.
/// </summary>
[GenerateSerializer]
public sealed record AgentJobInput(
    [property: Id(0)] string Prompt,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkspacePath = null,
    [property: Id(3)] string? ProjectId = null,
    /// <summary>
    /// Resolved Agent profile identity captured at launch time. Carried
    /// through to dispatch for traceability. Null when the job is a
    /// raw-prompt-only AgentJob.
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
    /// unset (workflow-shaped path or a raw-prompt-only validation
    /// dispatch).
    /// </summary>
    [property: Id(8)] string? AgentSessionId = null,
    /// <summary>
    /// Resolved Agent <c>variant</c> snapshot captured at launch time.
    /// Surfaced in the dispatch envelope so the runner can apply the
    /// launch-time variant to the runtime turn. Null when no Agent
    /// definition supplied a variant or the launch did not pin one.
    /// </summary>
    [property: Id(9)] string? Variant = null);

[GenerateSerializer]
public sealed record AgentJobTerminalResult(
    [property: Id(0)] AgentJobStatus Status,
    [property: Id(1)] string? Message,
    [property: Id(2)] string? Output,
    [property: Id(3)] string[]? ArtifactUploadIds,
    [property: Id(4)] string? FailureReason,
    [property: Id(5)] int? ExitCode);
