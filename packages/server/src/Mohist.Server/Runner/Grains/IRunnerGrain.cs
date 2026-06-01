namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    Task HeartbeatAsync();
    Task<WorkDispatch?> PeekAsync();
    Task<IReadOnlyList<WorkDispatch>> PeekAllAsync();
    Task<WorkDispatch?> PollAsync();
    Task<string?> ReportAsync(string workId, WorkDispatchResult result);
    Task<bool> IsAvailableAsync();
    Task AssignWorkflowAsync(string workflowRunId);
    Task RestoreLeasedWorkAsync(string workflowRunId, string workId, string workType, string stage, string? title);
    Task ReleaseAsync(string? workflowRunId = null);
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

public enum RunnerStatus { Online, Offline }
