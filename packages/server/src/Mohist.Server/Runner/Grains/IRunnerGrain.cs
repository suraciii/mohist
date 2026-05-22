namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    Task HeartbeatAsync();
    Task<WorkDispatch?> PeekAsync();
    Task<WorkDispatch?> PollAsync();
    Task<string?> ReportAsync(string workId, WorkDispatchResult result);
    Task<bool> IsAvailableAsync();
    Task DispatchAsync(WorkDispatch work);
    Task ReleaseAsync();
}

[GenerateSerializer]
public record RunnerInfo(string RunnerId, string[] Capabilities, string Hostname);

[GenerateSerializer]
public record WorkDispatch(string RunId, string Stage, string WorkId, string WorkType, string? Uses = null, string? With = null);

[GenerateSerializer]
public record WorkDispatchResult(string Status, string? Message = null, string? Output = null, int? ExitCode = null);

public enum RunnerStatus { Idle, Busy, Offline }
