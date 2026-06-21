using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Agent.Grains;
using Microsoft.EntityFrameworkCore;
using Orleans.Concurrency;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _projectId;
    private string? _pendingBuildGitHash;
    private readonly Dictionary<string, RunnerTrackedWork> _works = new(StringComparer.Ordinal);
    private DateTime _lastHeartbeat;
    private int _nextProjectIndex;
    private IDisposable? _heartbeatTimer;
    private readonly IWorkflowBacklogDirectory _backlogs;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(
        IWorkflowBacklogDirectory backlogs,
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<RunnerGrain> log)
    {
        _backlogs = backlogs;
        _dbFactory = dbFactory;
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
        var effectiveHash = info.BuildGitHash ?? _pendingBuildGitHash;
        _info = info with
        {
            MaxWorkflowSlots = RunnerCapacity.Normalize(info.MaxWorkflowSlots),
            BuildGitHash = effectiveHash,
        };
        _projectId = string.IsNullOrWhiteSpace(info.ProjectId) ? null : info.ProjectId;
        _status = RunnerStatus.Online;
        _lastHeartbeat = DateTime.UtcNow;
        _pendingBuildGitHash = null;
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

        await NotifyTrackedWorkflowRunnersLostAsync();
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
        {
            await RegisterAsync(info);
            return;
        }

        var effectiveHash = info.BuildGitHash ?? _pendingBuildGitHash;
        if (effectiveHash is not null && _info is not null && _info.BuildGitHash != effectiveHash)
        {
            _info = _info with { BuildGitHash = effectiveHash };
            var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
            await registry.RegisterAsync(_info);
        }
        _pendingBuildGitHash = null;
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
        _works.Values.Select(w => OwnerIdentityFor(w.Dispatch)).Distinct(StringComparer.Ordinal).Count();

    public Task<RunnerWorkAssignmentResult> AssignWorkAsync(WorkDispatch work)
    {
        if (_status == RunnerStatus.Offline)
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "offline"));

        switch (work.OwnerKind)
        {
            case WorkDispatchOwnerKinds.Workflow:
                return AssignWorkValidationForWorkflowAsync(work);
            case WorkDispatchOwnerKinds.AgentJob:
                return AssignWorkValidationForAgentJobAsync(work);
            default:
                return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));
        }
    }

    private Task<RunnerWorkAssignmentResult> AssignWorkValidationForWorkflowAsync(WorkDispatch work)
    {
        if (string.IsNullOrWhiteSpace(work.WorkflowRunId) || string.IsNullOrWhiteSpace(work.WorkId))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));

        var key = WorkKey(work.OwnerKind, OwnerIdentityFor(work), work.WorkId);
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

    private Task<RunnerWorkAssignmentResult> AssignWorkValidationForAgentJobAsync(WorkDispatch work)
    {
        if (string.IsNullOrWhiteSpace(work.AgentJobId) || string.IsNullOrWhiteSpace(work.WorkId))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));

        // Note: unlike the workflow arm above, the agent-job arm does not perform
        // "stale key" cleanup for the same AgentJobId. The AgentJobGrain model is
        // single-shot per AgentJobId (one work per job, see design Decision 4), so
        // there is no legitimate retry/upgrade path that would generate stale work
        // keys to remove. If a future contributor adds a multi-shot agent-job
        // model, they should re-introduce the workflow arm's stale-key cleanup.
        var key = WorkKey(work.OwnerKind, OwnerIdentityFor(work), work.WorkId);
        if (_works.ContainsKey(key))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));

        _works[key] = new RunnerTrackedWork(work, RunnerWorkState.Assigned, DateTimeOffset.UtcNow);
        _log.LogInformation("Runner {Id} assigned work {WorkId} for agent-job {AgentJobId}", RunnerId, work.WorkId, work.AgentJobId);
        return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));
    }

    public async Task<RunnerWorkReportResult> ReportResultAsync(WorkDispatch work, string workId, WorkResult result)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(work.WorkflowRunId, null, false, "missing-work", work.OwnerKind, OwnerIdentityFor(work));

        switch (work.OwnerKind)
        {
            case WorkDispatchOwnerKinds.Workflow:
                return await ReportResultForWorkflowAsync(work, workId, result);
            case WorkDispatchOwnerKinds.AgentJob:
                return await ReportResultForAgentJobAsync(work, workId, result);
            default:
                return new RunnerWorkReportResult(work.WorkflowRunId, null, false, "invalid-work", work.OwnerKind, OwnerIdentityFor(work));
        }
    }

    private async Task<RunnerWorkReportResult> ReportResultForWorkflowAsync(WorkDispatch work, string workId, WorkResult result)
    {
        var workflowRunId = work.WorkflowRunId;
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-workflow", work.OwnerKind, OwnerIdentityFor(work));
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-work", work.OwnerKind, OwnerIdentityFor(work));

        var key = WorkKey(work.OwnerKind, OwnerIdentityFor(work), workId);
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

    private async Task<RunnerWorkReportResult> ReportResultForAgentJobAsync(WorkDispatch work, string workId, WorkResult result)
    {
        if (string.IsNullOrWhiteSpace(work.AgentJobId))
            return new RunnerWorkReportResult(string.Empty, null, false, "missing-agent-job", work.OwnerKind, OwnerIdentityFor(work));
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(string.Empty, null, false, "missing-work", work.OwnerKind, OwnerIdentityFor(work));

        var key = WorkKey(work.OwnerKind, OwnerIdentityFor(work), workId);
        var tracked = _works.ContainsKey(key);

        var job = GrainFactory.GetGrain<IAgentJobGrain>(work.AgentJobId);
        var accepted = await job.ReportResultAsync(RunnerId, workId, result);

        if (tracked && accepted.Accepted)
            _works.Remove(key);

        var reason = !accepted.Accepted
            ? $"job-rejected:{accepted.Reason ?? "unknown"}"
            : tracked ? "reported" : "untracked";

        return new RunnerWorkReportResult(
            string.Empty,
            null,
            tracked,
            reason,
            work.OwnerKind,
            work.AgentJobId);
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
                .Select(ProjectActiveWork)
                .ToArray()));
    }

    private static RunnerActiveWorkItem ProjectActiveWork(RunnerTrackedWork work)
    {
        var dispatch = work.Dispatch;
        return new RunnerActiveWorkItem(
            WorkId: dispatch.WorkId,
            OwnerKind: dispatch.OwnerKind,
            OwnerId: OwnerIdentityFor(dispatch),
            WorkType: dispatch.WorkType,
            Stage: dispatch.Stage,
            Title: dispatch.Title,
            Issue: dispatch.Issue);
    }

    public async Task UpdateBuildGitHashAsync(string? buildGitHash)
    {
        var normalized = string.IsNullOrWhiteSpace(buildGitHash) ? null : buildGitHash.Trim();
        if (_info is null)
        {
            // Buffer the hash so a subsequent RegisterAsync can pick it up.
            _pendingBuildGitHash = normalized;
            return;
        }

        if (string.Equals(_info.BuildGitHash, normalized, StringComparison.Ordinal))
            return;

        _info = _info with { BuildGitHash = normalized };
        _log.LogInformation("Runner {Id} reported buildGitHash {Hash}", RunnerId, normalized ?? "<null>");
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.RegisterAsync(_info);
    }

    public Task<RunnerInfo?> GetInfoAsync()
    {
        return Task.FromResult(_info);
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
        await NotifyTrackedWorkflowRunnersLostAsync();
        _works.Clear();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKey());
        await registry.UnregisterAsync(RunnerId);
    }

    private async Task NotifyTrackedWorkflowRunnersLostAsync()
        => await NotifyTrackedWorkflowRunnersLostAsync(
            _works.Values.Select(w => w.Dispatch),
            RunnerId,
            workflowRunId => GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId).NotifyRunnerLostAsync(RunnerId),
            (ex, workflowRunId) => _log.LogWarning(ex,
                "Runner {RunnerId} failed to notify workflow {WorkflowRunId} about lost work",
                RunnerId,
                workflowRunId));

    internal static async Task NotifyTrackedWorkflowRunnersLostAsync(
        IEnumerable<WorkDispatch> trackedWork,
        string runnerId,
        Func<string, Task> notifyWorkflowAsync,
        Action<Exception, string> logFailure)
    {
        var workflowRunIds = trackedWork
            .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow)
            .Select(w => w.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var workflowRunId in workflowRunIds)
        {
            try
            {
                await notifyWorkflowAsync(workflowRunId);
            }
            catch (Exception ex)
            {
                logFailure(ex, workflowRunId);
            }
        }
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
                _log.LogDebug(
                    "Runner {Id} dropping work {WorkId} for {OwnerKind} {OwnerId}: not runnable",
                    RunnerId, selectedWork.Dispatch.WorkId, selectedWork.Dispatch.OwnerKind, OwnerIdentityFor(selectedWork.Dispatch));
                _works.Remove(selectedKey);
                continue;
            }

            _works[selectedKey] = selectedWork.MarkRunning(DateTimeOffset.UtcNow);
            return selectedWork.Dispatch;
        }
    }

    private async Task<bool> IsWorkRunnableAsync(WorkDispatch work)
    {
        switch (work.OwnerKind)
        {
            case WorkDispatchOwnerKinds.Workflow:
                return await IsWorkRunnableForWorkflowAsync(work);
            case WorkDispatchOwnerKinds.AgentJob:
                return await IsWorkRunnableForAgentJobAsync(work);
            default:
                return false;
        }
    }

    private async Task<bool> IsWorkRunnableForWorkflowAsync(WorkDispatch work)
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

    private async Task<bool> IsWorkRunnableForAgentJobAsync(WorkDispatch work)
    {
        if (string.IsNullOrWhiteSpace(work.AgentJobId))
            return false;
        var job = GrainFactory.GetGrain<IAgentJobGrain>(work.AgentJobId);
        return await job.IsWorkRunnableAsync(RunnerId, work.WorkId);
    }

    private static string OwnerIdentityFor(WorkDispatch work) => work.OwnerKind switch
    {
        WorkDispatchOwnerKinds.AgentJob => work.AgentJobId ?? string.Empty,
        _ => work.WorkflowRunId,
    };

    private static string WorkKey(string ownerKind, string ownerId, string workId) => $"{ownerKind}\u001f{ownerId}\u001f{workId}";
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
