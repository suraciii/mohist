using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Microsoft.EntityFrameworkCore;
using Orleans.Concurrency;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _projectId;
    private readonly Queue<WorkDispatch> _pendingWorks = new();
    private readonly Dictionary<string, WorkDispatch> _trackedWork = new(StringComparer.Ordinal);
    private DateTime _lastHeartbeat;
    private int _nextProjectIndex;
    private IDisposable? _heartbeatTimer;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly IDbContextFactory<MohistDbContext>? _dbFactory;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(
        IWorkflowBacklogDirectory backlogs,
        IServiceProvider services,
        ILogger<RunnerGrain> log)
    {
        _backlogs = backlogs;
        _dbFactory = services.GetService<IDbContextFactory<MohistDbContext>>();
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
        _info = info with { MaxWorkflowSlots = RunnerCapacity.Normalize(info.MaxWorkflowSlots) };
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
        await registry.RegisterAsync(_info);
        _heartbeatTimer ??= this.RegisterGrainTimer(
            _ => CheckHeartbeatAsync(),
            HeartbeatCheckInterval,
            HeartbeatCheckInterval);
        _log.LogInformation("Runner {Id} registered from {Host} for {Scope} with {Slots} workflow slots", info.RunnerId, info.Hostname, _projectId ?? "all projects", _info.MaxWorkflowSlots);
    }

    public async Task UnregisterAsync()
    {
        _log.LogInformation("Runner {Id} unregistered", RunnerId);

        _status = RunnerStatus.Offline;
        _info = null;
        _pendingWorks.Clear();
        _trackedWork.Clear();
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);
    }

    public async Task HeartbeatAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        await TouchPresenceAsync();
    }

    public async Task HeartbeatRepairAsync(RunnerInfo info)
    {
        if (_status != RunnerStatus.Online)
            await RegisterAsync(info);
        else
            await TouchPresenceAsync();
    }

    public async Task<WorkDispatch?> PollAsync()
    {
        if (_status == RunnerStatus.Offline)
            throw new InvalidOperationException($"Runner '{RunnerId}' is offline");

        await TouchPresenceAsync();

        var pending = await DequeuePendingWorkAsync();
        if (pending is not null)
            return pending;

        if (ActiveWorkflowCount >= MaxWorkflowSlots)
            return null;

        while (true)
        {
            var claimed = await ClaimFromBacklogAsync();
            if (string.IsNullOrWhiteSpace(claimed))
                break;

            var claimedWork = await DequeuePendingWorkAsync();
            if (claimedWork is not null)
                return claimedWork;
        }

        return null;
    }

    private int MaxWorkflowSlots => RunnerCapacity.Normalize(_info?.MaxWorkflowSlots);

    private int ActiveWorkflowCount =>
        _trackedWork.Values.Select(w => w.WorkflowRunId).Distinct(StringComparer.Ordinal).Count();

    public Task<RunnerWorkAssignmentResult> AssignWorkAsync(WorkDispatch work)
    {
        if (_status == RunnerStatus.Offline)
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "offline"));

        if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));

        var key = WorkKey(work.WorkflowRunId, work.WorkId);
        if (_trackedWork.ContainsKey(key))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));

        var staleKeys = _trackedWork.Keys
            .Where(k => _trackedWork[k].WorkflowRunId == work.WorkflowRunId)
            .ToArray();
        if (staleKeys.Length > 0)
        {
            foreach (var sk in staleKeys)
                _trackedWork.Remove(sk);
            _log.LogDebug("Runner {Id} replaced {Count} stale work entries for workflow {WorkflowId}",
                RunnerId, staleKeys.Length, work.WorkflowRunId);
        }

        _trackedWork[key] = work;
        _pendingWorks.Enqueue(work);
        _log.LogInformation("Runner {Id} assigned work {WorkId} for workflow {WorkflowId}", RunnerId, work.WorkId, work.WorkflowRunId);
        return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));
    }

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Online);
    }

    public async Task<RunnerRuntimeState> GetRuntimeStateAsync()
    {
        await DequeuePendingWorkAsync();
        return new RunnerRuntimeState(
            _status,
            _lastHeartbeat,
            _trackedWork.Values
                .Select(w => w.WorkflowRunId)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
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
        _pendingWorks.Clear();
        _trackedWork.Clear();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);
    }

    private string RunnerRegistryKey() => _projectId ?? RunnerRegistryKeys.Global;

    private async Task TouchPresenceAsync()
    {
        _lastHeartbeat = DateTime.UtcNow;
        if (_info is null) return;

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.RegisterAsync(_info);
    }

    private async Task<IReadOnlyList<string>> BacklogProjectIdsAsync()
    {
        if (!string.IsNullOrWhiteSpace(_projectId))
            return [_projectId];

        var projectIds = new HashSet<string>(_backlogs.ListProjects(), StringComparer.Ordinal);
        if (_dbFactory is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var persistedProjectIds = await db.Projects
                .AsNoTracking()
                .Select(project => project.Id)
                .ToListAsync();
            foreach (var projectId in persistedProjectIds)
            {
                if (!string.IsNullOrWhiteSpace(projectId))
                    projectIds.Add(projectId);
            }
        }

        return projectIds.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task<string?> ClaimFromBacklogAsync()
    {
        var projectIds = await BacklogProjectIdsAsync();
        if (projectIds.Count == 0)
            return null;

        var start = _nextProjectIndex % projectIds.Count;
        for (var offset = 0; offset < projectIds.Count; offset++)
        {
            var index = (start + offset) % projectIds.Count;
            var projectId = projectIds[index];
            var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(projectId));
            var claimed = await backlog.ClaimAsync(RunnerId);
            if (!string.IsNullOrWhiteSpace(claimed))
            {
                _nextProjectIndex = (index + 1) % projectIds.Count;
                return claimed;
            }
        }

        _nextProjectIndex = (start + 1) % projectIds.Count;
        return null;
    }

    private async Task<WorkDispatch?> DequeuePendingWorkAsync()
    {
        while (_pendingWorks.Count > 0)
        {
            var work = _pendingWorks.Dequeue();
            var key = WorkKey(work.WorkflowRunId, work.WorkId);

            if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
                continue;

            if (!_trackedWork.TryGetValue(key, out _))
                continue;

            if (!await IsTrackedWorkValidAsync(work))
            {
                _trackedWork.Remove(key);
                continue;
            }

            return work;
        }

        var pendingKeys = new HashSet<string>(
            _pendingWorks.Select(p => WorkKey(p.WorkflowRunId, p.WorkId)),
            StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var (key, work) in _trackedWork)
        {
            if (pendingKeys.Contains(key))
                continue;

            if (!await IsTrackedWorkValidAsync(work))
                stale.Add(key);
        }

        foreach (var key in stale)
            _trackedWork.Remove(key);

        return null;
    }

    private async Task<bool> IsTrackedWorkValidAsync(WorkDispatch work)
    {
        var wf = GrainFactory.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var owner = await wf.GetClaimedRunnerIdAsync();
        var status = await wf.GetRunStatusAsync();
        if (owner != RunnerId || status != "Running")
            return false;

        var currentWorkId = await wf.GetCurrentWorkIdAsync();
        return string.Equals(currentWorkId, work.WorkId, StringComparison.Ordinal);
    }

    private static string WorkKey(string workflowRunId, string workId) => $"{workflowRunId}\u001f{workId}";
}
