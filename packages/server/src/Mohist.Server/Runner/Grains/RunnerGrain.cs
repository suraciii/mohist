using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Runner.Grains;

public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _projectId;
    private readonly HashSet<string> _assignedWorkflows = [];
    private readonly Dictionary<string, string> _workToWorkflow = new();
    private readonly Dictionary<string, WorkDispatch> _workById = new();
    private readonly Dictionary<string, string> _workToProject = new();
    private DateTime _lastHeartbeat;
    private IDisposable? _heartbeatTimer;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(IWorkflowBacklogDirectory backlogs, ILogger<RunnerGrain> log)
    {
        _backlogs = backlogs;
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
        var previousRegistryKey = _info is null ? null : RunnerRegistryKey();
        _info = info;
        _projectId = string.IsNullOrWhiteSpace(info.ProjectId) ? null : info.ProjectId;
        _status = RunnerStatus.Online;
        _lastHeartbeat = DateTime.UtcNow;
        var registryKey = RunnerRegistryKey();
        if (previousRegistryKey is not null && previousRegistryKey != registryKey)
        {
            var previousRegistry = GrainFactory.GetGrain<IRunnerRegistryGrain>(previousRegistryKey);
            await previousRegistry.UnregisterAsync(RunnerId);
        }

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(registryKey);
        await registry.RegisterAsync(info);
        _heartbeatTimer ??= this.RegisterGrainTimer(
            _ => CheckHeartbeatAsync(),
            HeartbeatCheckInterval,
            HeartbeatCheckInterval);
        _log.LogInformation("Runner {Id} registered from {Host} for {Scope}", info.RunnerId, info.Hostname, _projectId ?? "all projects");
    }

    public async Task UnregisterAsync()
    {
        _log.LogInformation("Runner {Id} unregistered", RunnerId);

        var workflows = _assignedWorkflows.ToList();
        var workMappings = _workToWorkflow.ToList();
        var agentWorkMappings = workMappings.Where(kv => IsAgentWork(kv.Key)).ToList();
        var workProjects = new Dictionary<string, string>(_workToProject, StringComparer.Ordinal);
        _status = RunnerStatus.Offline;
        _info = null;
        _assignedWorkflows.Clear();
        _workToWorkflow.Clear();
        _workById.Clear();
        _workToProject.Clear();
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);

        foreach (var wfId in workflows)
        {
            _log.LogWarning("Runner {Id} unregistered with in-flight work, failing workflow {WorkflowId}", RunnerId, wfId);
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            await workflowGrain.AbandonCurrentWorkAsync(RunnerId, $"Runner {RunnerId} unregistered");
        }

        foreach (var (workId, workflowRunId) in agentWorkMappings)
        {
            if (!workProjects.TryGetValue(workId, out var projectId)) continue;
            var session = GrainFactory.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, workId));
            await session.FailIfRunningAsync($"Runner {RunnerId} unregistered");
        }
    }

    public async Task HeartbeatAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        await TouchPresenceAsync();
    }

    public async Task<WorkDispatch?> PollAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        await TouchPresenceAsync();

        foreach (var wfId in _assignedWorkflows)
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            var work = await workflow.GetWorkAsync(RunnerId);
            if (work is not null)
            {
                _workToWorkflow[work.WorkId] = wfId;
                _workById[work.WorkId] = work;
                TrackWorkProject(work);
                return work;
            }
        }

        foreach (var projectId in BacklogProjectIds())
        {
            var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(GrainKey.WorkflowBacklog(projectId));
            var claimedId = await backlog.ClaimAsync(RunnerId);
            if (claimedId is not null)
            {
                _assignedWorkflows.Add(claimedId);
                var workflow = GrainFactory.GetGrain<IWorkflowGrain>(claimedId);
                var work = await workflow.GetWorkAsync(RunnerId);
                if (work is not null)
                {
                    _workToWorkflow[work.WorkId] = claimedId;
                    _workById[work.WorkId] = work;
                    TrackWorkProject(work, projectId);
                    return work;
                }
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
        if (_status == RunnerStatus.Online)
            await TouchPresenceAsync();

        if (!_workToWorkflow.Remove(workId, out var wfId))
            return null;
        _workById.Remove(workId);
        _workToProject.Remove(workId);

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
        await workflow.ReportResultAsync(RunnerId, workId, result);

        var status = await workflow.GetRunStatusAsync();
        if (status is "Completed" or "Failed")
        {
            _assignedWorkflows.Remove(wfId);
            var staleKeys = _workToWorkflow
                .Where(kv => kv.Value == wfId)
                .Select(kv => kv.Key)
                .ToList();
                foreach (var key in staleKeys)
                {
                    _workToWorkflow.Remove(key);
                    _workById.Remove(key);
                    _workToProject.Remove(key);
                }
            _log.LogInformation("Runner {Id} released terminal workflow {WorkflowId} (status={Status})", RunnerId, wfId, status);
        }

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
            {
                _workToWorkflow.Remove(key);
                _workById.Remove(key);
                _workToProject.Remove(key);
            }
        }
        else
        {
            _assignedWorkflows.Clear();
            _workToWorkflow.Clear();
            _workById.Clear();
            _workToProject.Clear();
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
        var timedOutWork = _workToWorkflow.ToList();
        var timedOutAgentWork = timedOutWork.Where(kv => IsAgentWork(kv.Key)).ToList();
        var workProjects = new Dictionary<string, string>(_workToProject, StringComparer.Ordinal);
        _assignedWorkflows.Clear();
        _workToWorkflow.Clear();
        _workById.Clear();
        _workToProject.Clear();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);

        foreach (var wfId in timedOutWorkflows)
        {
            _log.LogWarning("Runner {Id} timed out, failing in-flight work for workflow {WorkflowId}", RunnerId, wfId);
            var workflowGrain = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            await workflowGrain.AbandonCurrentWorkAsync(RunnerId, $"Runner heartbeat timeout after {HeartbeatTimeout.TotalSeconds}s");
        }

        foreach (var (workId, workflowRunId) in timedOutAgentWork)
        {
            if (!workProjects.TryGetValue(workId, out var projectId)) continue;
            var session = GrainFactory.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, workId));
            await session.FailIfRunningAsync($"Runner heartbeat timeout after {HeartbeatTimeout.TotalSeconds}s");
        }
    }

    private bool IsAgentWork(string workId)
    {
        return _workById.TryGetValue(workId, out var work) && work.Uses == "mohist/acp-agent";
    }

    private string RunnerRegistryKey() => _projectId ?? RunnerRegistryKeys.Global;

    private async Task TouchPresenceAsync()
    {
        _lastHeartbeat = DateTime.UtcNow;
        if (_info is null) return;

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.RegisterAsync(_info);
    }

    private IReadOnlyList<string> BacklogProjectIds()
    {
        if (!string.IsNullOrWhiteSpace(_projectId))
            return [_projectId];

        return _backlogs.ListProjects();
    }

    private void TrackWorkProject(WorkDispatch work, string? fallbackProjectId = null)
    {
        var projectId = work.Issue?.ProjectId ?? fallbackProjectId ?? _projectId;
        if (!string.IsNullOrWhiteSpace(projectId))
            _workToProject[work.WorkId] = projectId;
    }
}
