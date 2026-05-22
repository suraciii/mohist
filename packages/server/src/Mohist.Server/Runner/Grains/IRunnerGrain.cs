using System.Text.Json;

namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync();
    Task<WorkDispatch?> PollAsync();
    Task ReportAsync(string workId, WorkDispatchResult result);
    Task<bool> IsAvailableAsync();
    Task DispatchAsync(WorkDispatch work);
    Task ReleaseAsync();
}

public record RunnerInfo(
    string RunnerId,
    string[] Capabilities,
    string Hostname);

public record WorkDispatch(
    string RunId,
    string Stage,
    string WorkId,
    string WorkType,
    string? Uses,
    Dictionary<string, JsonElement?>? With);

public record WorkDispatchResult(
    string Status,
    string? Message = null,
    JsonElement? Output = null,
    int? ExitCode = null);

public enum RunnerStatus { Idle, Busy, Offline }
