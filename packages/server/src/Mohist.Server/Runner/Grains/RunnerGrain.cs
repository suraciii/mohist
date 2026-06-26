using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
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
    private readonly Dictionary<string, RunnerWorkflowWork> _outstandingWorkflowWorks = new(StringComparer.Ordinal);
    private DateTime _lastHeartbeat;
    private IDisposable? _heartbeatTimer;

    // Authoritative source for dispatch capacity. Loaded from the persisted
    // definition state in OnActivateAsync / RegisterAsync and updated via
    // UpdateAsync (write-through). A value reported by the runner process
    // via register/heartbeat SHALL NOT influence this field.
    private int? _slots;

    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly RunnerDefinitionStore _definitions;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);

    public RunnerGrain(
        WorkflowRunQuerier workflowRuns,
        RunnerDefinitionStore definitions,
        WorkflowItemTranslator translator,
        ILogger<RunnerGrain> log)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _translator = translator;
        _log = log;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
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
        _info = info with
        {
            BuildGitHash = effectiveHash,
        };
        _status = RunnerStatus.Online;
        _lastHeartbeat = DateTime.UtcNow;
        _pendingBuildGitHash = null;
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

        var run = await _workflowRuns.LoadAsync(workflowRunId);
        if (run is null)
            return new RunnerWorkReportResult(workflowRunId, null, false, "missing-workflow", WorkDispatchOwnerKinds.Workflow, workflowRunId);

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
        var key = WorkflowWorkKey(workflowRunId, workId);
        var tracked = _outstandingWorkflowWorks.TryGetValue(key, out var existing)
            ? existing
            : await RecoverActiveWorkflowWorkAsync(workflow, workflowRunId, workId, run);
        if (tracked is null)
            return new RunnerWorkReportResult(workflowRunId, null, false, "untracked", WorkDispatchOwnerKinds.Workflow, workflowRunId);

        var outcome = await _translator.TranslateResultAsync(tracked.Item, result, workflowRunId, run);

        switch (outcome)
        {
            case WorkflowItemTranslator.InboundOutcome.Task task:
                await workflow.ReportTaskOutcomeAsync(RunnerId, workId, task.Value);
                break;
            case WorkflowItemTranslator.InboundOutcome.Checks checks:
                await workflow.ReportCheckOutcomeAsync(RunnerId, workId, checks.Value);
                break;
        }

        _outstandingWorkflowWorks.Remove(key);
        var workflowStatus = await workflow.GetRunStatusAsync();

        return new RunnerWorkReportResult(
            workflowRunId,
            workflowStatus,
            true,
            "reported",
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId);
    }

    private async Task<RunnerWorkflowWork?> RecoverActiveWorkflowWorkAsync(
        IWorkflowGrain workflow,
        string workflowRunId,
        string workId,
        WorkflowRun run)
    {
        var active = await workflow.GetActiveWorkAsync(workId);
        if (active is null || !string.Equals(active.WorkId, workId, StringComparison.Ordinal))
            return null;

        WorkItem? item = active.WorkType switch
        {
            WorkItemTypes.Task => RecoverActiveTaskWorkItem(run, active),
            WorkItemTypes.Checks => RecoverActiveChecksWorkItem(run, active),
            _ => null,
        };
        if (item is null)
            return null;

        var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, RunnerId);
        return new RunnerWorkflowWork(item, dispatch, DateTimeOffset.UtcNow);
    }

    private static WorkItem? RecoverActiveTaskWorkItem(WorkflowRun run, WorkflowActiveWorkView active)
    {
        var stage = run.Stages.FirstOrDefault(s => string.Equals(s.Id, active.Stage, StringComparison.Ordinal));
        var task = stage?.Tasks.FirstOrDefault(t => string.Equals(t.WorkId ?? t.Id, active.WorkId, StringComparison.Ordinal));
        return task is null
            ? null
            : WorkItem.Task(active.Stage, active.WorkId, task.Title, task.Uses, task.WithInput, task.Artifacts, task.SetVars);
    }

    private static WorkItem? RecoverActiveChecksWorkItem(WorkflowRun run, WorkflowActiveWorkView active)
    {
        var stage = run.Stages.FirstOrDefault(s => string.Equals(s.Id, active.Stage, StringComparison.Ordinal));
        if (stage is null)
            return null;

        var pendingChecks = stage.Checks
            .Where(c => c.Status == StageCheckStatus.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        return WorkItem.Checks(active.Stage, active.WorkId, pendingChecks);
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

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task UpdateAsync(int slots)
    {
        if (slots <= 0)
            throw new ArgumentOutOfRangeException(nameof(slots), slots, "slots must be a positive integer");

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
        // Snapshot the outstanding-work set so concurrent report/poll activity
        // does not mutate it mid-iteration. Each entry is synthesized as a
        // failed report through the normal ReportWorkflowResultAsync channel,
        // which routes via the runner-side translator and the workflow grain's
        // regular ReportTaskOutcome/ReportCheckOutcome entry points — the
        // grain sees an ordinary failure, indistinguishable from a runner
        // process that ran the work and reported `failed` itself.
        if (_outstandingWorkflowWorks.Count == 0)
            return;

        var snapshot = _outstandingWorkflowWorks
            .Select(kv => (Key: kv.Key, WorkflowRunId: ExtractWorkflowRunId(kv.Key), WorkId: ExtractWorkId(kv.Key)))
            .ToList();

        var synthesizedFailure = new WorkResult("failed", "runner-lost");
        foreach (var entry in snapshot)
        {
            try
            {
                await ReportWorkflowResultAsync(entry.WorkflowRunId, entry.WorkId, synthesizedFailure);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to synthesize failed report for workflow {WorkflowRunId} work {WorkId}",
                    RunnerId,
                    entry.WorkflowRunId,
                    entry.WorkId);
            }
        }
    }

    private static string ExtractWorkflowRunId(string key)
    {
        var separator = key.IndexOf('\u001f');
        return separator < 0 ? key : key.Substring(0, separator);
    }

    private static string ExtractWorkId(string key)
    {
        var separator = key.IndexOf('\u001f');
        return separator < 0 ? string.Empty : key.Substring(separator + 1);
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

            var dispatch = await PollOneWorkflowAsync(workflow, workflowRunId);
            if (dispatch is not null)
                return dispatch;
        }

        foreach (var workflowRunId in await _workflowRuns.FindAssignableAsync(_info?.ProjectId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            var assigned = await workflow.AssignRunnerAsync(RunnerId);
            if (assigned.Status != WorkflowAssignmentStatus.Assigned)
                continue;

            var dispatch = await PollOneWorkflowAsync(workflow, workflowRunId);
            if (dispatch is not null)
                return dispatch;
        }

        return null;
    }

    private async Task<WorkDispatch?> PollOneWorkflowAsync(IWorkflowGrain workflow, string workflowRunId)
    {
        var item = await workflow.PollWorkAsync(RunnerId);
        if (item is null) return null;

        var run = await _workflowRuns.LoadAsync(workflowRunId);
        if (run is null) return null;

        var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, RunnerId);
        var key = WorkflowWorkKey(workflowRunId, item);
        _outstandingWorkflowWorks[key] = new RunnerWorkflowWork(item, dispatch, DateTimeOffset.UtcNow);
        return dispatch;
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

    private static string WorkflowWorkKey(string workflowRunId, string workId) =>
        $"{workflowRunId}\u001f{workId}";

    private static string WorkflowWorkKey(string workflowRunId, WorkItem item) =>
        $"{workflowRunId}\u001f{item.Id}";
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

/// <summary>
/// Tracks a workflow work item the runner pulled from the control plane.
/// Mirrors <see cref="RunnerTrackedWork"/> (agent-job accounting) but for
/// workflow work. The grain returns domain <see cref="WorkItem"/>s; the
/// runner translates them into <see cref="WorkDispatch"/> and remembers
/// the original <see cref="WorkItem"/> so the inbound <see cref="WorkResult"/>
/// can be matched to <see cref="TaskWorkItem"/> / <see cref="ChecksWorkItem"/>
/// when the runner process reports back. Removing the entry on successful
/// report keeps the set authoritative for runner-loss closeout (T-004).
/// </summary>
internal sealed record RunnerWorkflowWork(
    WorkItem Item,
    WorkDispatch Dispatch,
    DateTimeOffset PolledAt);
