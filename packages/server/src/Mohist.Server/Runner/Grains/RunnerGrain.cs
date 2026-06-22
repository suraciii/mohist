using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
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
    private string? _pendingBuildGitHash;
    private readonly Dictionary<string, RunnerTrackedWork> _agentJobs = new(StringComparer.Ordinal);
    private DateTime _lastHeartbeat;
    private IDisposable? _heartbeatTimer;

    // Authoritative source for dispatch capacity. Loaded from the persisted
    // definition state in OnActivateAsync / RegisterAsync and updated via
    // UpdateAsync (write-through). A value reported by the runner process
    // via register/heartbeat SHALL NOT influence this field.
    private int? _slots;

    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly RunnerDefinitionStore _definitions;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(
        WorkflowRunQuerier workflowRuns,
        RunnerDefinitionStore definitions,
        ILogger<RunnerGrain> log)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _log = log;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        // Reload persisted slots on every activation so the in-memory cache
        // survives grain deactivation followed by reacquisition. This is the
        // structural fix for the capacity-volatility issue: a runner's slots
        // are now sourced from the persisted definition, not from in-memory
        // heartbeat state.
        _slots = await _definitions.GetOrInitAsync(RunnerId, ct);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        return Task.CompletedTask;
    }

    public async Task RegisterAsync(RunnerInfo info)
    {
        var effectiveHash = info.BuildGitHash ?? _pendingBuildGitHash;
        // The runner-reported MaxWorkflowSlots field is intentionally NOT
        // written into the persisted definition state. Persisted slots are
        // the sole authoritative source; this field is preserved on the
        // RunnerInfo record for runner-line compatibility only.
        _info = info with
        {
            BuildGitHash = effectiveHash,
        };
        _status = RunnerStatus.Online;
        _lastHeartbeat = DateTime.UtcNow;
        _pendingBuildGitHash = null;
        // Hydrate the slots cache from the persisted definition state on
        // every register (covers both first-time register and post-restart
        // re-register). A runner-reported slots value is ignored.
        _slots = await _definitions.GetOrInitAsync(RunnerId);
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.RegisterAsync(_info);
        _heartbeatTimer ??= this.RegisterGrainTimer(
            _ => CheckHeartbeatAsync(),
            HeartbeatCheckInterval,
            HeartbeatCheckInterval);
        _log.LogInformation("Runner {Id} registered from {Host} as global resource with {Slots} persisted workflow slots", info.RunnerId, info.Hostname, _slots);
    }

    public async Task UnregisterAsync()
    {
        _log.LogInformation("Runner {Id} unregistered", RunnerId);

        await NotifyTrackedWorkflowRunnersLostAsync();
        _status = RunnerStatus.Offline;
        _info = null;
        _agentJobs.Clear();
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
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
            var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
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

        var pending = await DequeueAssignedAgentJobAsync();
        if (pending is not null)
            return pending;

        if (await ActiveWorkflowCountAsync() >= MaxWorkflowSlots)
            return null;

        return await PollAssignedOrAssignableWorkflowAsync();
    }

    private int MaxWorkflowSlots =>
        _slots ?? RunnerCapacity.DefaultMaxWorkflowSlots;

    private async Task<int> ActiveWorkflowCountAsync()
    {
        var count = 0;
        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(RunnerId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            var currentWorkId = await workflow.GetCurrentWorkIdAsync();
            if (!string.IsNullOrWhiteSpace(currentWorkId))
                count++;
        }
        return count;
    }

    public Task<RunnerWorkAssignmentResult> AssignAgentJobAsync(WorkDispatch work)
    {
        if (work.OwnerKind != WorkDispatchOwnerKinds.AgentJob)
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));
        if (string.IsNullOrWhiteSpace(work.AgentJobId) || string.IsNullOrWhiteSpace(work.WorkId))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work"));

        // Note: unlike the workflow arm above, the agent-job arm does not perform
        // "stale key" cleanup for the same AgentJobId. The AgentJobGrain model is
        // single-shot per AgentJobId (one work per job, see design Decision 4), so
        // there is no legitimate retry/upgrade path that would generate stale work
        // keys to remove. If a future contributor adds a multi-shot agent-job
        // model, they should re-introduce the workflow arm's stale-key cleanup.
        var key = WorkKey(work.OwnerKind, OwnerIdentityFor(work), work.WorkId);
        if (_agentJobs.ContainsKey(key))
            return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));

        _agentJobs[key] = new RunnerTrackedWork(work, RunnerWorkState.Assigned, DateTimeOffset.UtcNow);
        _log.LogInformation("Runner {Id} assigned work {WorkId} for agent-job {AgentJobId}", RunnerId, work.WorkId, work.AgentJobId);
        return Task.FromResult(new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned));
    }

    public async Task<RunnerWorkReportResult> ReportWorkflowResultAsync(string workflowRunId, string workId, WorkResult result)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-work", WorkDispatchOwnerKinds.Workflow, workflowRunId);
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-workflow", WorkDispatchOwnerKinds.Workflow, workflowRunId);

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.ReportResultAsync(RunnerId, workId, result);
        var workflowStatus = await workflow.GetRunStatusAsync();

        return new RunnerWorkReportResult(
            workflowRunId,
            workflowStatus,
            true,
            "reported",
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId);
    }

    public async Task<RunnerWorkReportResult> ReportAgentJobResultAsync(string agentJobId, string workId, WorkResult result)
    {
        if (string.IsNullOrWhiteSpace(agentJobId))
            return new RunnerWorkReportResult(string.Empty, null, false, "missing-agent-job", WorkDispatchOwnerKinds.AgentJob, agentJobId);
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(string.Empty, null, false, "missing-work", WorkDispatchOwnerKinds.AgentJob, agentJobId);

        var key = WorkKey(WorkDispatchOwnerKinds.AgentJob, agentJobId, workId);
        var tracked = _agentJobs.ContainsKey(key);

        var job = GrainFactory.GetGrain<IAgentJobGrain>(agentJobId);
        var accepted = await job.ReportResultAsync(RunnerId, workId, result);

        if (tracked && accepted.Accepted)
            _agentJobs.Remove(key);

        var reason = !accepted.Accepted
            ? $"job-rejected:{accepted.Reason ?? "unknown"}"
            : tracked ? "reported" : "untracked";

        return new RunnerWorkReportResult(
            string.Empty,
            null,
            tracked,
            reason,
            WorkDispatchOwnerKinds.AgentJob,
            agentJobId);
    }

    public async Task<RunnerRuntimeState> GetRuntimeStateAsync()
    {
        var activeWorks = new List<RunnerActiveWorkItem>();
        activeWorks.AddRange(_agentJobs.Values.Select(ProjectActiveWork));
        activeWorks.AddRange(await ProjectActiveWorkflowWorksAsync());

        return new RunnerRuntimeState(
            _status,
            _lastHeartbeat,
            activeWorks);
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

    private async Task<IReadOnlyList<RunnerActiveWorkItem>> ProjectActiveWorkflowWorksAsync()
    {
        var items = new List<RunnerActiveWorkItem>();
        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(RunnerId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            var currentWorkId = await workflow.GetCurrentWorkIdAsync();
            if (string.IsNullOrWhiteSpace(currentWorkId))
                continue;

            var active = await workflow.GetActiveWorkAsync(currentWorkId);
            if (active is null)
                continue;

            items.Add(new RunnerActiveWorkItem(
                WorkId: active.WorkId,
                OwnerKind: WorkDispatchOwnerKinds.Workflow,
                OwnerId: workflowRunId,
                WorkType: active.WorkType,
                Stage: active.Stage,
                Title: active.Title,
                Issue: string.IsNullOrWhiteSpace(active.ProjectId)
                    || string.IsNullOrWhiteSpace(active.IssueId)
                    || active.IssueNumber is null
                    ? null
                    : new WorkIssueRef(active.ProjectId, active.IssueId, active.IssueNumber.Value)));
        }

        return items;
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
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.RegisterAsync(_info);
    }

    public Task<RunnerInfo?> GetInfoAsync()
    {
        return Task.FromResult(_info);
    }

    public Task<int> GetSlotsAsync()
    {
        return Task.FromResult(MaxWorkflowSlots);
    }

    public async Task UpdateAsync(int slots)
    {
        if (slots <= 0)
            throw new ArgumentOutOfRangeException(nameof(slots), slots, "slots must be a positive integer");

        // Write-through: persist first so the next dispatch cycle is
        // guaranteed to observe the new value even if a subsequent caller
        // hits a freshly reactivated grain before the cache update is
        // visible. The cache update is best-effort within the same call.
        await _definitions.UpdateSlotsAsync(RunnerId, slots);
        _slots = slots;
        _log.LogInformation("Runner {Id} slots updated to {Slots}", RunnerId, slots);
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
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.UnregisterAsync(RunnerId);
    }

    private async Task NotifyTrackedWorkflowRunnersLostAsync()
    {
        var workflowRunIds = await _workflowRuns.FindAssignedToAsync(RunnerId);
        foreach (var workflowRunId in workflowRunIds)
        {
            try
            {
                await GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId).NotifyRunnerLostAsync(RunnerId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to notify workflow {WorkflowRunId} about lost runner",
                    RunnerId,
                    workflowRunId);
            }
        }
    }

    private async Task TouchPresenceAsync()
    {
        _lastHeartbeat = DateTime.UtcNow;
        if (_info is null) return;

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.RegisterAsync(_info);
    }

    private async Task<WorkDispatch?> PollAssignedOrAssignableWorkflowAsync()
    {
        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(RunnerId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            var currentWorkId = await workflow.GetCurrentWorkIdAsync();
            if (!string.IsNullOrWhiteSpace(currentWorkId))
                continue;

            var work = await workflow.PollWorkAsync(RunnerId);
            if (work is not null)
                return work;
        }

        foreach (var workflowRunId in await _workflowRuns.FindAssignableAsync(_info?.ProjectId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            var assigned = await workflow.AssignRunnerAsync(RunnerId);
            if (assigned.Status != WorkflowAssignmentStatus.Assigned)
                continue;

            var work = await workflow.PollWorkAsync(RunnerId);
            if (work is not null)
                return work;
        }

        return null;
    }

    private async Task<WorkDispatch?> DequeueAssignedAgentJobAsync()
    {
        while (true)
        {
            string? selectedKey = null;
            RunnerTrackedWork? selectedWork = null;

            foreach (var (key, work) in _agentJobs)
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
                _agentJobs.Remove(selectedKey);
                continue;
            }

            _agentJobs[selectedKey] = selectedWork.MarkRunning(DateTimeOffset.UtcNow);
            return selectedWork.Dispatch;
        }
    }

    private async Task<bool> IsWorkRunnableAsync(WorkDispatch work)
    {
        switch (work.OwnerKind)
        {
            case WorkDispatchOwnerKinds.AgentJob:
                return await IsWorkRunnableForAgentJobAsync(work);
            default:
                return false;
        }
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
