using System.Text.Json;
using Mohist.Server.Workflow.Grains;
using Orleans.Concurrency;

namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    Task HeartbeatAsync();
    Task HeartbeatRepairAsync(RunnerInfo info);
    Task<RunnerWorkAssignmentResult> AssignAgentJobAsync(WorkDispatch work);
    Task<WorkDispatch?> PollAsync();
    Task<RunnerWorkReportResult> ReportWorkflowResultAsync(string workflowRunId, string workId, WorkResult result);
    Task<RunnerWorkReportResult> ReportAgentJobResultAsync(string agentJobId, string workId, WorkResult result);
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
    Task DeactivateForTestAsync();
}

public static class RunnerCapacity
{
    public const int DefaultMaxWorkflowSlots = 1;

    public static int Normalize(int? maxWorkflowSlots) =>
        maxWorkflowSlots is > 0 ? maxWorkflowSlots.Value : DefaultMaxWorkflowSlots;
}

[GenerateSerializer]
public record RunnerInfo(
    string RunnerId,
    string[] Capabilities,
    string Hostname,
    string? ProjectId,
    string[]? CoderModels = null,
    string Kind = "external",
    DateTimeOffset? RegisteredAt = null,
    /// <summary>
    /// Reported dispatch capacity from the runner process. Non-authoritative:
    /// the persisted definition state (queried via <see cref="IRunnerGrain.GetSlotsAsync"/>)
    /// is the sole source of dispatch capacity. This field is preserved for
    /// runner-line compatibility and registry telemetry only.
    /// </summary>
    int MaxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null);

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
    [property: Id(14)] string? Recovery = null)
{
    public WorkDispatch() : this(string.Empty, string.Empty) { }
}

public static class WorkDispatchOwnerKinds
{
    public const string Workflow = "workflow";
    public const string AgentJob = "agent-job";
}

[GenerateSerializer]
public record WorkIssueRef(
    string ProjectId,
    string IssueId,
    int IssueNumber);

[GenerateSerializer]
public record WorkResult(
    string Status,
    string? Message = null,
    string? Output = null,
    int? ExitCode = null,
    string[]? ArtifactUploadIds = null,
    [property: Id(5)] List<RuntimeTaskInput>? AddTasks = null);

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
