namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    Task HeartbeatAsync();
    Task HeartbeatRepairAsync(RunnerInfo info);
    Task<RunnerWorkAssignmentResult> AssignWorkAsync(WorkDispatch work);
    Task<WorkDispatch?> PollAsync();
    Task<string?> ReportAsync(string workId, WorkDispatchResult result, string? workflowRunId = null);
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
    WorkIssueRef? Issue = null);

[GenerateSerializer]
public record WorkIssueRef(
    string ProjectId,
    string IssueId,
    int IssueNumber);

[GenerateSerializer]
public record WorkDispatchResult(string Status, string? Message = null, string? Output = null, int? ExitCode = null);

[GenerateSerializer]
public sealed record RunnerWorkAssignmentResult(
    [property: Id(0)] RunnerWorkAssignmentStatus Status,
    [property: Id(1)] string? Reason = null);

public enum RunnerWorkAssignmentStatus
{
    Assigned,
    Rejected
}

public enum RunnerStatus { Online, Offline }

[GenerateSerializer]
public record RunnerRuntimeState(
    RunnerStatus Status,
    DateTimeOffset LastHeartbeatAt,
    IReadOnlyList<WorkDispatch> ActiveWork);
