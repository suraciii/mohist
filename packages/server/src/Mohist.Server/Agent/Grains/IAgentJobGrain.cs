using System.Text.Json;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public interface IAgentJobGrain : IGrainWithStringKey
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
    [property: Id(5)] bool RunnerAccepted = false);

public enum AgentJobStatus
{
    Pending,
    Running,
    Completed,
    Failed
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

public static class AgentJobFailureReasons
{
    public const string RunnerUnavailable = "runner-unavailable";
    public const string ReportTimeout = "report-timeout";
}
