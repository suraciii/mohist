using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Grains;
using Microsoft.EntityFrameworkCore;
using Orleans.Concurrency;
using System.Text.Json;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _projectId;
    private readonly Queue<WorkDispatch> _pendingWorks = new();
    private readonly Dictionary<string, string> _workToWorkflow = new();
    private readonly Dictionary<string, WorkDispatch> _workById = new();
    private readonly Dictionary<string, string> _workToProject = new();
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

        var workMappings = _workToWorkflow.ToList();
        var agentWorkMappings = workMappings.Where(kv => IsAgentWork(kv.Key)).ToList();
        var workProjects = new Dictionary<string, string>(_workToProject, StringComparer.Ordinal);
        var agentSessions = agentWorkMappings.Select(kv =>
        {
            var sessionName = _workById.TryGetValue(kv.Key, out var work) ? AgentSessionName(work) : kv.Key;
            return (WorkId: kv.Key, WorkflowRunId: kv.Value, SessionName: sessionName);
        }).ToList();
        _status = RunnerStatus.Offline;
        _info = null;
        _pendingWorks.Clear();
        _workToWorkflow.Clear();
        _workById.Clear();
        _workToProject.Clear();
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);

        foreach (var (workId, workflowRunId, sessionName) in agentSessions)
        {
            if (!workProjects.TryGetValue(workId, out var projectId)) continue;
            var session = GrainFactory.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, sessionName));
            await session.FailIfRunningAsync($"Runner {RunnerId} unregistered");
        }
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

        await CleanupInactiveWorkAsync();

        while (await ActiveWorkflowCountAsync() < MaxWorkflowSlots)
        {
            var claimed = await ClaimFromBacklogAsync();
            if (string.IsNullOrWhiteSpace(claimed))
                return null;

            var claimedWork = await DequeuePendingWorkAsync();
            if (claimedWork is not null)
                return claimedWork;
        }

        return null;
    }

    private int MaxWorkflowSlots => RunnerCapacity.Normalize(_info?.MaxWorkflowSlots);

    private async Task<int> ActiveWorkflowCountAsync()
    {
        await Task.CompletedTask;
        return _workToWorkflow.Values
            .Concat(_pendingWorks.Select(work => work.WorkflowRunId))
            .Where(workflowRunId => !string.IsNullOrWhiteSpace(workflowRunId))
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    public Task<RunnerWorkAssignmentResult> AssignWorkAsync(WorkDispatch work)
    {
        if (_status == RunnerStatus.Offline)
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "offline"));

        if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));

        var key = WorkKey(work.WorkflowRunId, work.WorkId);
        if (_workById.ContainsKey(key)
            || _pendingWorks.Any(p => string.Equals(WorkKey(p.WorkflowRunId, p.WorkId), key, StringComparison.Ordinal)))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));

        if (_workToWorkflow.Values.Contains(work.WorkflowRunId, StringComparer.Ordinal)
            || _pendingWorks.Any(p => string.Equals(p.WorkflowRunId, work.WorkflowRunId, StringComparison.Ordinal)))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "workflow-busy"));

        _pendingWorks.Enqueue(work);
        TrackWorkProject(work);
        _log.LogInformation("Runner {Id} assigned work {WorkId} for workflow {WorkflowId}", RunnerId, work.WorkId, work.WorkflowRunId);
        return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));
    }

    public async Task<string?> ReportAsync(string workId, WorkDispatchResult result, string? workflowRunId = null)
    {
        if (_status == RunnerStatus.Online)
            await TouchPresenceAsync();

        var lookupKey = workflowRunId is null ? workId : WorkKey(workflowRunId, workId);
        if (!_workToWorkflow.TryGetValue(lookupKey, out var wfId)
            && !_workToWorkflow.TryGetValue(workId, out wfId))
            wfId = workflowRunId;
        if (string.IsNullOrWhiteSpace(wfId))
            return null;
        RemoveWorkTracking(wfId, workId);

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
        await workflow.ReportResultAsync(RunnerId, workId, result);

        return wfId;
    }

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_status == RunnerStatus.Online);
    }

    public async Task<RunnerRuntimeState> GetRuntimeStateAsync()
    {
        await CleanupInactiveWorkAsync();
        var activeWork = _workById.Values
            .GroupBy(work => WorkKey(work.WorkflowRunId, work.WorkId), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        return new RunnerRuntimeState(
            _status,
            _lastHeartbeat,
            activeWork);
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
        var timedOutWork = _workToWorkflow.ToList();
        var timedOutAgentWork = timedOutWork.Where(kv => IsAgentWork(kv.Key)).ToList();
        var workProjects = new Dictionary<string, string>(_workToProject, StringComparer.Ordinal);
        var timedOutAgentSessions = timedOutAgentWork.Select(kv =>
        {
            var sessionName = _workById.TryGetValue(kv.Key, out var work) ? AgentSessionName(work) : kv.Key;
            return (WorkId: kv.Key, WorkflowRunId: kv.Value, SessionName: sessionName);
        }).ToList();
        _pendingWorks.Clear();
        _workToWorkflow.Clear();
        _workById.Clear();
        _workToProject.Clear();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);

        foreach (var (workId, workflowRunId, sessionName) in timedOutAgentSessions)
        {
            if (!workProjects.TryGetValue(workId, out var projectId)) continue;
            var session = GrainFactory.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, sessionName));
            await session.FailIfRunningAsync($"Runner heartbeat timeout after {HeartbeatTimeout.TotalSeconds}s");
        }
    }

    private bool IsAgentWork(string workId)
    {
        return _workById.TryGetValue(workId, out var work) && work.Uses == "mohist/acp-agent";
    }

    private static string AgentSessionName(WorkDispatch work)
    {
        if (string.IsNullOrWhiteSpace(work.With)) return work.WorkId;

        try
        {
            using var document = JsonDocument.Parse(work.With);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("session", out var session)
                && session.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(session.GetString()))
                return session.GetString()!;
        }
        catch (JsonException)
        {
        }

        return work.WorkId;
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

    private void TrackWorkProject(WorkDispatch work, string? fallbackProjectId = null)
    {
        var projectId = work.Issue?.ProjectId ?? fallbackProjectId ?? _projectId;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            _workToProject[work.WorkId] = projectId;
            _workToProject[WorkKey(work.WorkflowRunId, work.WorkId)] = projectId;
        }
    }

    private void TrackWork(string workflowRunId, WorkDispatch work)
    {
        _workToWorkflow[work.WorkId] = workflowRunId;
        _workToWorkflow[WorkKey(workflowRunId, work.WorkId)] = workflowRunId;
        _workById[work.WorkId] = work;
        _workById[WorkKey(workflowRunId, work.WorkId)] = work;
        TrackWorkProject(work);
    }

    private async Task<WorkDispatch?> DequeuePendingWorkAsync()
    {
        while (_pendingWorks.Count > 0)
        {
            var work = _pendingWorks.Dequeue();
            if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
                continue;

            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
            var current = await IsCurrentWorkAsync(work);
            if (!current.Valid)
            {
                RemoveWorkTracking(work.WorkflowRunId, work.WorkId);
                continue;
            }

            if (!string.Equals(current.WorkId, work.WorkId, StringComparison.Ordinal))
            {
                RemoveWorkTracking(work.WorkflowRunId, work.WorkId);
                await workflow.AssignRunnerAsync(RunnerId);
                continue;
            }

            var key = WorkKey(work.WorkflowRunId, work.WorkId);
            if (_workById.ContainsKey(key))
                continue;

            TrackWork(work.WorkflowRunId, work);
            return work;
        }

        return null;
    }

    private async Task CleanupInactiveWorkAsync()
    {
        var activeWork = _workById.Values
            .GroupBy(work => WorkKey(work.WorkflowRunId, work.WorkId), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        foreach (var work in activeWork)
        {
            var current = await IsCurrentWorkAsync(work);
            if (current.Valid && string.Equals(current.WorkId, work.WorkId, StringComparison.Ordinal))
                continue;

            RemoveWorkTracking(work.WorkflowRunId, work.WorkId);
        }
    }

    private async Task<(bool Valid, string? WorkId)> IsCurrentWorkAsync(WorkDispatch work)
    {
        if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
            return (false, null);

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var owner = await workflow.GetClaimedRunnerIdAsync();
        var status = await workflow.GetRunStatusAsync();
        var currentWorkId = await workflow.GetCurrentWorkIdAsync();
        if (!string.Equals(owner, RunnerId, StringComparison.Ordinal)
            || !string.Equals(status, "Running", StringComparison.Ordinal))
            return (false, currentWorkId);

        return (true, currentWorkId);
    }

    private static string WorkKey(string workflowRunId, string workId) => $"{workflowRunId}\u001f{workId}";

    private void RemoveWorkTracking(string workflowRunId, string workId)
    {
        RemoveWorkTrackingKey(workId);
        RemoveWorkTrackingKey(WorkKey(workflowRunId, workId));
    }

    private void RemoveWorkflowTracking(string workflowRunId)
    {
        var keys = _workToWorkflow
            .Where(kv => kv.Value == workflowRunId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in keys)
            RemoveWorkTrackingKey(key);
    }

    private void RemoveWorkTrackingKey(string key)
    {
        _workToWorkflow.Remove(key);
        _workById.Remove(key);
        _workToProject.Remove(key);
    }
}
