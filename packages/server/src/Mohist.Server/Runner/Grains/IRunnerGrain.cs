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
    Task<RunnerWorkReportResult> ReportResultAsync(string workflowRunId, string workId, WorkResult result);
    Task<bool> IsAvailableAsync();
    Task<RunnerRuntimeState> GetRuntimeStateAsync();
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
    int MaxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots);

[GenerateSerializer]
public record WorkDispatch(
    string WorkflowRunId,
    string WorkId,
    string? Uses = null,
    string? With = null,
    string? Variables = null,
    string WorkType = "task",
    string? Stage = null,
    string? Title = null,
    WorkIssueRef? Issue = null,
    string? Artifacts = null,
    string? Outputs = null);

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
    [property: Id(3)] string? Reason = null);

public enum RunnerStatus { Online, Offline }

[GenerateSerializer]
public record RunnerRuntimeState(
    RunnerStatus Status,
    DateTimeOffset LastHeartbeatAt,
    IReadOnlyList<string> ActiveWorkflowRunIds);
