using System.Text.Json;

namespace Mohist.Server.Runner.Grains;

public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private WorkDispatch? _pending;
    private WorkDispatchResult? _result;
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

    public Task<WorkDispatch?> PollAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        var work = _pending;
        if (work is not null)
        {
            _status = RunnerStatus.Busy;
            _pending = null;
            _log.LogInformation("Runner {Id} picked up work {WorkId}", RunnerId, work.WorkId);
        }
        return Task.FromResult(work);
    }

    public Task ReportAsync(string workId, WorkDispatchResult result)
    {
        _result = result;
        _status = RunnerStatus.Idle;
        _log.LogInformation("Runner {Id} reported work {WorkId}: {Status}", RunnerId, workId, result.Status);
        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Idle);
    }

    public Task<bool> TryDispatchAsync(WorkDispatch work)
    {
        if (_status != RunnerStatus.Idle)
            return Task.FromResult(false);

        _pending = work;
        _log.LogInformation("Work {WorkId} dispatched to runner {Id}", work.WorkId, RunnerId);
        return Task.FromResult(true);
    }
}
