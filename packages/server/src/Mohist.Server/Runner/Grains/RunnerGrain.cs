using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Grains;

public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private readonly HashSet<string> _assignedWorkflows = [];
    private DateTime _lastHeartbeat;
    private IDisposable? _heartbeatTimer;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(ILogger<RunnerGrain> log)
    {
        _log = log;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override Task OnActivateAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        return Task.CompletedTask;
    }

    public async Task RegisterAsync(RunnerInfo info)
    {
        _info = info;
        _status = RunnerStatus.Online;
        _lastHeartbeat = DateTime.UtcNow;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        await registry.RegisterAsync(RunnerId, info.Capabilities);
        _heartbeatTimer ??= this.RegisterGrainTimer(
            _ => CheckHeartbeatAsync(),
            HeartbeatCheckInterval,
            HeartbeatCheckInterval);
        _log.LogInformation("Runner {Id} registered from {Host}", info.RunnerId, info.Hostname);
    }

    public async Task UnregisterAsync()
    {
        _log.LogInformation("Runner {Id} unregistered", RunnerId);
        _status = RunnerStatus.Offline;
        _info = null;
        _assignedWorkflows.Clear();
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        await registry.UnregisterAsync(RunnerId);
    }

    public Task HeartbeatAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        _lastHeartbeat = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public async Task<WorkDispatch?> PollAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        _lastHeartbeat = DateTime.UtcNow;

        foreach (var wfId in _assignedWorkflows)
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            var work = await workflow.GetWorkAsync();
            if (work is not null)
                return work;
        }

        return null;
    }

    public Task<WorkDispatch?> PeekAsync()
    {
        return PollAsync();
    }

    public Task<IReadOnlyList<WorkDispatch>> PeekAllAsync()
    {
        return Task.FromResult<IReadOnlyList<WorkDispatch>>([]);
    }

#pragma warning disable CS8602
    public async Task<string?> ReportAsync(string workId, WorkDispatchResult result)
    {
        foreach (var wfId in _assignedWorkflows)
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            await workflow.ReportResultAsync(workId, result);
            return wfId;
        }

        return null;
    }
#pragma warning restore CS8602

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Online);
    }

    public Task AssignWorkflowAsync(string workflowRunId)
    {
        _assignedWorkflows.Add(workflowRunId);
        _log.LogInformation("Runner {Id} assigned to workflow {WorkflowId}", RunnerId, workflowRunId);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string? workflowRunId = null)
    {
        if (workflowRunId is not null)
            _assignedWorkflows.Remove(workflowRunId);
        else
            _assignedWorkflows.Clear();

        _log.LogInformation("Runner {Id} released workflow {WorkflowId}", RunnerId, workflowRunId ?? "*");
        return Task.CompletedTask;
    }

    private async Task CheckHeartbeatAsync()
    {
        if (_status == RunnerStatus.Offline) return;

        var elapsed = DateTime.UtcNow - _lastHeartbeat;
        if (elapsed > HeartbeatTimeout)
        {
            _log.LogWarning("Runner {Id} heartbeat timeout ({Elapsed}s)", RunnerId, elapsed.TotalSeconds);
            await HandleTimeoutAsync();
        }
    }

#pragma warning disable CS8602
    private async Task HandleTimeoutAsync()
    {
        var timedOutWorkflows = _assignedWorkflows.ToList();
        _assignedWorkflows.Clear();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        await registry.UnregisterAsync(RunnerId);

        foreach (var wfId in timedOutWorkflows)
        {
            _log.LogWarning("Runner {Id} timed out, reporting failure for workflow {WorkflowId}", RunnerId, wfId);
            var result = new WorkDispatchResult("failed", $"Runner heartbeat timeout after {HeartbeatTimeout.TotalSeconds}s");
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            await workflowGrain.ReportResultAsync("", result);
        }
    }
#pragma warning restore CS8602
}
