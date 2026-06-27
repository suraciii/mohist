using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public interface IAgentJobGrain : IGrainWithStringKey
{
    Task<bool> IsWorkRunnableAsync(string runnerId, string workId);
    Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result);
    Task<AgentJobStatus> GetStatusAsync();
    Task<string?> GetCurrentWorkIdAsync();
    Task AssignRunnerAsync(string runnerId, string workId);
    Task SubmitAsync(AgentJobInput input);
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
    [property: Id(4)] int DispatchAttempts = 0);

public enum AgentJobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

[GenerateSerializer]
public sealed record AgentJobInput(
    [property: Id(0)] string Prompt,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkspacePath = null,
    [property: Id(3)] string? ProjectId = null,
    [property: Id(4)] string? Uses = null);

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
