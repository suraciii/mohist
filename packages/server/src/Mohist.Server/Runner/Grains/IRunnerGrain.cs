using System.Text.Json;
using Orleans.Concurrency;

namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    Task HeartbeatAsync();
    Task HeartbeatRepairAsync(RunnerInfo info);
    [AlwaysInterleave]
    Task<RunnerWorkAssignmentResult> AssignWorkAsync(WorkDispatch work);
    Task<WorkDispatch?> PollAsync();
    Task<RunnerWorkReportResult> ReportResultAsync(WorkDispatch work, string workId, WorkResult result);
    Task<bool> IsAvailableAsync();
    Task<RunnerRuntimeState> GetRuntimeStateAsync();
    Task UpdateBuildGitHashAsync(string? buildGitHash);
    Task<RunnerInfo?> GetInfoAsync();
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
    int MaxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots,
    string? BuildGitHash = null);

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
    [property: Id(10)] string? Outputs = null,
    [property: Id(11)] string OwnerKind = WorkDispatchOwnerKinds.Workflow,
    [property: Id(12)] string? AgentJobId = null)
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
    Dictionary<string, JsonElement>? CapturedOutputs = null);

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
    IReadOnlyList<string> ActiveWorkflowRunIds);
