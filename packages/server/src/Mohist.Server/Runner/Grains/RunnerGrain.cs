using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
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
    private readonly Dictionary<string, RunnerTrackedWork> _works = new(StringComparer.Ordinal);
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
        _works.Clear();
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

        var pending = await DequeueAssignedWorkAsync();
        if (pending is not null)
            return pending;

        if (ActiveWorkflowCount >= MaxWorkflowSlots)
            return null;

        while (true)
        {
            var claimed = await ClaimFromBacklogAsync();
            if (string.IsNullOrWhiteSpace(claimed))
                break;

            var claimedWork = await DequeueAssignedWorkAsync();
            if (claimedWork is not null)
                return claimedWork;
        }

        return null;
    }

    private int MaxWorkflowSlots => RunnerCapacity.Normalize(_info?.MaxWorkflowSlots);

    private int ActiveWorkflowCount =>
        _works.Values.Select(w => w.Dispatch.WorkflowRunId).Distinct(StringComparer.Ordinal).Count();

    public Task<RunnerWorkAssignmentResult> AssignWorkAsync(WorkDispatch work)
    {
        if (_status == RunnerStatus.Offline)
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "offline"));

        if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));

        var key = WorkKey(work.WorkflowRunId, work.WorkId);
        if (_works.ContainsKey(key))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));

        var staleKeys = _works.Keys
            .Where(k => _works[k].Dispatch.WorkflowRunId == work.WorkflowRunId)
            .ToArray();
        if (staleKeys.Length > 0)
        {
            foreach (var sk in staleKeys)
                _works.Remove(sk);
            _log.LogDebug("Runner {Id} replaced {Count} stale work entries for workflow {WorkflowId}",
                RunnerId, staleKeys.Length, work.WorkflowRunId);
        }

        _works[key] = new RunnerTrackedWork(work, RunnerWorkState.Assigned, DateTimeOffset.UtcNow);
        _log.LogInformation("Runner {Id} assigned work {WorkId} for workflow {WorkflowId}", RunnerId, work.WorkId, work.WorkflowRunId);
        return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));
    }

    public async Task<RunnerWorkReportResult> ReportResultAsync(string workflowRunId, string workId, WorkResult result)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-workflow");
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-work");

        var key = WorkKey(workflowRunId, workId);
        var tracked = _works.ContainsKey(key);

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.ReportResultAsync(RunnerId, workId, result);
        var workflowStatus = await workflow.GetRunStatusAsync();

        if (tracked)
            _works.Remove(key);

        return new RunnerWorkReportResult(
            workflowRunId,
            workflowStatus,
            tracked,
            tracked ? "reported" : "untracked");
    }

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Online);
    }

    public Task<RunnerRuntimeState> GetRuntimeStateAsync()
    {
        return Task.FromResult(new RunnerRuntimeState(
            _status,
            _lastHeartbeat,
            _works.Values
                .Select(w => w.Dispatch.WorkflowRunId)
                .Distinct(StringComparer.Ordinal)
                .ToArray()));
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
        _works.Clear();
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

    private async Task<WorkDispatch?> DequeueAssignedWorkAsync()
    {
        while (true)
        {
            string? selectedKey = null;
            RunnerTrackedWork? selectedWork = null;

            foreach (var (key, work) in _works)
            {
                if (work.Status != RunnerWorkState.Assigned)
                    continue;

                selectedKey = key;
                selectedWork = work;
                break;
            }

            if (selectedKey is null || selectedWork is null)
                return null;

            if (!await IsWorkRunnableAsync(selectedWork.Dispatch))
            {
                _works.Remove(selectedKey);
                continue;
            }

            _works[selectedKey] = selectedWork.MarkRunning(DateTimeOffset.UtcNow);
            return selectedWork.Dispatch;
        }
    }

    private async Task<bool> IsWorkRunnableAsync(WorkDispatch work)
    {
        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var owner = await workflow.GetClaimedRunnerIdAsync();
        if (!string.Equals(owner, RunnerId, StringComparison.Ordinal))
            return false;

        var status = await workflow.GetRunStatusAsync();
        if (!string.Equals(status, "Running", StringComparison.Ordinal))
            return false;

        var currentWorkId = await workflow.GetCurrentWorkIdAsync();
        return string.Equals(currentWorkId, work.WorkId, StringComparison.Ordinal);
    }

    private static string WorkKey(string workflowRunId, string workId) => $"{workflowRunId}\u001f{workId}";
}

internal enum RunnerWorkState
{
    Assigned,
    Running
}

internal sealed record RunnerTrackedWork(
    WorkDispatch Dispatch,
    RunnerWorkState Status,
    DateTimeOffset AssignedAt,
    DateTimeOffset? StartedAt = null)
{
    public RunnerTrackedWork MarkRunning(DateTimeOffset now) =>
        this with { Status = RunnerWorkState.Running, StartedAt = StartedAt ?? now };
}
