using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Grains;

public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private readonly HashSet<string> _assignedWorkflows = [];
    private readonly Dictionary<string, string> _workToWorkflow = new();
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

        var workflows = _assignedWorkflows.ToList();
        _status = RunnerStatus.Offline;
        _info = null;
        _assignedWorkflows.Clear();
        _workToWorkflow.Clear();
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        await registry.UnregisterAsync(RunnerId);

        foreach (var wfId in workflows)
        {
            _log.LogWarning("Runner {Id} unregistered with in-flight work, failing workflow {WorkflowId}", RunnerId, wfId);
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            await workflowGrain.FailInFlightWorkAsync($"Runner {RunnerId} unregistered");
        }
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
            {
                _workToWorkflow[work.WorkId] = wfId;
                return work;
            }
        }

        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key);
        var claimedId = await backlog.ClaimAsync(RunnerId);
        if (claimedId is not null)
        {
            _assignedWorkflows.Add(claimedId);
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(claimedId);
            await workflow.AssignRunnerAsync(RunnerId);
            var work = await workflow.GetWorkAsync();
            if (work is not null)
            {
                _workToWorkflow[work.WorkId] = claimedId;
                return work;
            }
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

    public async Task<string?> ReportAsync(string workId, WorkDispatchResult result)
    {
        if (!_workToWorkflow.Remove(workId, out var wfId))
            return null;

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
        await workflow.ReportResultAsync(workId, result);
        return wfId;
    }

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
        {
            _assignedWorkflows.Remove(workflowRunId);
            var staleKeys = _workToWorkflow
                .Where(kv => kv.Value == workflowRunId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in staleKeys)
                _workToWorkflow.Remove(key);
        }
        else
        {
            _assignedWorkflows.Clear();
            _workToWorkflow.Clear();
        }

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

    private async Task HandleTimeoutAsync()
    {
        var timedOutWorkflows = _assignedWorkflows.ToList();
        _assignedWorkflows.Clear();
        _workToWorkflow.Clear();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        await registry.UnregisterAsync(RunnerId);

        foreach (var wfId in timedOutWorkflows)
        {
            _log.LogWarning("Runner {Id} timed out, failing in-flight work for workflow {WorkflowId}", RunnerId, wfId);
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            await workflowGrain.FailInFlightWorkAsync($"Runner heartbeat timeout after {HeartbeatTimeout.TotalSeconds}s");
        }
    }
}
