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
    Task ReleaseAsync(string? workflowRunId = null);
}

[GenerateSerializer]
public record RunnerInfo(string RunnerId, string[] Capabilities, string Hostname);

[GenerateSerializer]
public record WorkDispatch(
    string WorkflowRunId,
    string WorkId,
    string? Uses = null,
    string? With = null,
    string WorkType = "task",
    string? Stage = null,
    string? Title = null);

[GenerateSerializer]
public record WorkDispatchResult(string Status, string? Message = null, string? Output = null, int? ExitCode = null);

public enum RunnerStatus { Online, Offline }
