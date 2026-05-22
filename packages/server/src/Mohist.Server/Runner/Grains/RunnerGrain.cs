using System.Text.Json;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private WorkDispatch? _work;
    private bool _polled;
    private DateTime _lastHeartbeat;
    private IDisposable? _heartbeatTimer;
    private readonly IRunnerRegistry _registry;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(IRunnerRegistry registry, ILogger<RunnerGrain> log)
    {
        _registry = registry;
        _log = log;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override Task OnActivateAsync(CancellationToken ct)
    {
        _heartbeatTimer = RegisterTimer(
            CheckHeartbeatAsync,
            null,
            HeartbeatCheckInterval,
            HeartbeatCheckInterval);
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        return Task.CompletedTask;
    }

    public Task RegisterAsync(RunnerInfo info)
    {
        _info = info;
        _status = RunnerStatus.Idle;
        _lastHeartbeat = DateTime.UtcNow;
        _registry.Register(RunnerId, info.Capabilities);
        _log.LogInformation("Runner {Id} registered from {Host}", info.RunnerId, info.Hostname);
        return Task.CompletedTask;
    }

    public Task UnregisterAsync()
    {
        _log.LogInformation("Runner {Id} unregistered", RunnerId);
        _status = RunnerStatus.Offline;
        _info = null;
        _work = null;
        _registry.Unregister(RunnerId);
        return Task.CompletedTask;
    }

    public Task HeartbeatAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        _lastHeartbeat = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task DispatchAsync(WorkDispatch work)
    {
        if (_status != RunnerStatus.Idle)
            throw new InvalidOperationException($"Runner '{RunnerId}' is {_status}, cannot dispatch");

        _work = work;
        _polled = false;
        _status = RunnerStatus.Busy;
        _log.LogInformation("Work {WorkId} dispatched to runner {Id}", work.WorkId, RunnerId);
        return Task.CompletedTask;
    }

    public Task<WorkDispatch?> PollAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        _lastHeartbeat = DateTime.UtcNow;

        if (_polled || _work is null)
            return Task.FromResult<WorkDispatch?>(null);

        _polled = true;
        return Task.FromResult<WorkDispatch?>(_work);
    }

    public async Task ReportAsync(string workId, WorkDispatchResult result)
    {
        _status = RunnerStatus.Idle;
        _lastHeartbeat = DateTime.UtcNow;

        _log.LogInformation("Runner {Id} reported work {WorkId}: {Status}", RunnerId, workId, result.Status);

        var runId = _work?.RunId;
        _work = null;

        if (runId is not null)
        {
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(runId);
            await workflowGrain.ReportResultAsync(workId, result);
        }
    }

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Idle);
    }

    public Task ReleaseAsync()
    {
        _work = null;
        _status = RunnerStatus.Idle;
        _log.LogInformation("Runner {Id} released", RunnerId);
        return Task.CompletedTask;
    }

    private async Task CheckHeartbeatAsync(object? _)
    {
        if (_status == RunnerStatus.Offline) return;

        var elapsed = DateTime.UtcNow - _lastHeartbeat;
        if (elapsed > HeartbeatTimeout)
        {
            _log.LogWarning("Runner {Id} heartbeat timeout ({Elapsed}s)", RunnerId, elapsed.TotalSeconds);
            await HandleTimeoutAsync();
        }
    }

    private async Task HandleTimeoutAsync()
    {
        var timedOutWork = _work;
        _work = null;
        _status = RunnerStatus.Offline;
        _registry.Unregister(RunnerId);

        if (timedOutWork is not null)
        {
            _log.LogWarning("Runner {Id} timed out during work {WorkId}, reporting failure", RunnerId, timedOutWork.WorkId);

            var result = new WorkDispatchResult("failed", $"Runner heartbeat timeout after {HeartbeatTimeout.TotalSeconds}s");
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(timedOutWork.RunId);
            await workflowGrain.ReportResultAsync(timedOutWork.WorkId, result);
        }
    }
}
