using System.Text.Json;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private WorkDispatch? _pending;
    private readonly Dictionary<string, WorkDispatchResult> _results = new();
    private readonly ILogger<RunnerGrain> _log;

    public RunnerGrain(ILogger<RunnerGrain> log)
    {
        _log = log;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public Task RegisterAsync(RunnerInfo info)
    {
        _info = info;
        _status = RunnerStatus.Idle;
        _log.LogInformation("Runner {Id} registered from {Host}", info.RunnerId, info.Hostname);
        return Task.CompletedTask;
    }

    public Task UnregisterAsync()
    {
        _log.LogInformation("Runner {Id} unregistered", RunnerId);
        _status = RunnerStatus.Offline;
        _info = null;
        return Task.CompletedTask;
    }

    public Task DispatchAsync(WorkDispatch work)
    {
        if (_status != RunnerStatus.Idle)
            throw new InvalidOperationException($"Runner '{RunnerId}' is {_status}, cannot dispatch");

        _pending = work;
        _status = RunnerStatus.Busy;
        _log.LogInformation("Work {WorkId} dispatched to runner {Id}", work.WorkId, RunnerId);
        return Task.CompletedTask;
    }

    public Task<WorkDispatch?> PollAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        var work = _pending;
        _pending = null;
        return Task.FromResult(work);
    }

    public Task ReportAsync(string workId, WorkDispatchResult result)
    {
        _results[workId] = result;
        _status = RunnerStatus.Idle;
        _log.LogInformation("Runner {Id} reported work {WorkId}: {Status}", RunnerId, workId, result.Status);
        return Task.CompletedTask;
    }

    public Task<WorkDispatchResult?> TryGetResultAsync(string workId)
    {
        _results.TryGetValue(workId, out var result);
        if (result is not null)
            _results.Remove(workId);
        return Task.FromResult(result);
    }

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Idle);
    }

    public Task ReleaseAsync()
    {
        _pending = null;
        _status = RunnerStatus.Idle;
        _log.LogInformation("Runner {Id} released", RunnerId);
        return Task.CompletedTask;
    }
}
