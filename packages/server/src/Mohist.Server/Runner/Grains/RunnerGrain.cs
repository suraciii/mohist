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
using Orleans.Runtime;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _pendingBuildGitHash;
    private readonly IPersistentState<RunnerWorksState> _worksState;
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
        ILogger<RunnerGrain> log,
        [PersistentState("runner-works")] IPersistentState<RunnerWorksState> worksState)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _translator = translator;
        _log = log;
        _worksState = worksState;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _slots = await _definitions.GetOrInitAsync(RunnerId, ct);
        if (!_worksState.RecordExists)
            await _worksState.ReadStateAsync();
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
        await ClearWorksAsync(WorkDispatchOwnerKinds.AgentJob);
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
        var persistedCount = GetWorks()
            .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow && w.Status == RunnerWorkStatus.Running)
            .Select(w => w.OwnerId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (persistedCount > 0)
            return persistedCount;

        var dbCount = 0;
        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(RunnerId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            var currentWorkId = await workflow.GetCurrentWorkIdAsync();
            if (!string.IsNullOrWhiteSpace(currentWorkId))
                dbCount++;
        }
        return dbCount;
    }

    public async Task<RunnerWorkAssignmentResult> AssignAgentJobAsync(WorkDispatch work)
    {
        if (work.OwnerKind != WorkDispatchOwnerKinds.AgentJob)
            return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work");
        if (string.IsNullOrWhiteSpace(work.AgentJobId) || string.IsNullOrWhiteSpace(work.WorkId))
            return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work");

        var ownerId = work.AgentJobId!;
        var existing = FindWork(work.WorkId, WorkDispatchOwnerKinds.AgentJob, ownerId);
        if (existing is not null)
            return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned);

        AddWork(new RunnerWork
        {
            WorkId = work.WorkId,
            OwnerKind = WorkDispatchOwnerKinds.AgentJob,
            OwnerId = ownerId,
            WorkType = work.WorkType,
            Stage = work.Stage,
            Title = work.Title,
            Issue = work.Issue,
            Status = RunnerWorkStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchSnapshot = work,
        });
        await PersistAsync();
        _log.LogInformation("Runner {Id} assigned work {WorkId} for agent-job {AgentJobId}", RunnerId, work.WorkId, work.AgentJobId);
        return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned);
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
        var trackedWork = FindWork(workId, WorkDispatchOwnerKinds.Workflow, workflowRunId);
        var tracked = trackedWork is not null;
        RunnerWorkflowWork? trackedEntry = null;

        if (trackedWork is not null)
        {
            var item = await RecoverWorkItemFromActiveWorkAsync(workflow, workflowRunId, workId, run);
            if (item is not null)
            {
                var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, RunnerId);
                trackedEntry = new RunnerWorkflowWork(item, dispatch, trackedWork.CreatedAt);
            }
        }
        else
        {
            trackedEntry = await RecoverActiveWorkflowWorkAsync(workflow, workflowRunId, workId, run);
        }

        if (trackedEntry is null)
            return new RunnerWorkReportResult(workflowRunId, null, false, "untracked", WorkDispatchOwnerKinds.Workflow, workflowRunId);

        var outcome = await _translator.TranslateResultAsync(trackedEntry.Item, result, workflowRunId, run);

        switch (outcome)
        {
            case WorkflowItemTranslator.InboundOutcome.Task task:
                await workflow.ReportTaskOutcomeAsync(RunnerId, workId, task.Value);
                break;
            case WorkflowItemTranslator.InboundOutcome.Checks checks:
                await workflow.ReportCheckOutcomeAsync(RunnerId, workId, checks.Value);
                break;
        }

        if (tracked)
        {
            TryRemoveWork(workId, WorkDispatchOwnerKinds.Workflow, workflowRunId);
            await PersistAsync();
        }

        var workflowStatus = await workflow.GetRunStatusAsync();

        return new RunnerWorkReportResult(
            workflowRunId,
            workflowStatus,
            tracked,
            "reported",
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId);
    }

    private async Task<WorkItem?> RecoverWorkItemFromActiveWorkAsync(
        IWorkflowGrain workflow,
        string workflowRunId,
        string workId,
        WorkflowRun run)
    {
        var active = await workflow.GetActiveWorkAsync(workId);
        if (active is null || !string.Equals(active.WorkId, workId, StringComparison.Ordinal))
            return null;

        return active.WorkType switch
        {
            WorkItemTypes.Task => RecoverActiveTaskWorkItem(run, active),
            WorkItemTypes.Checks => RecoverActiveChecksWorkItem(run, active),
            _ => null,
        };
    }

    private async Task<RunnerWorkflowWork?> RecoverActiveWorkflowWorkAsync(
        IWorkflowGrain workflow,
        string workflowRunId,
        string workId,
        WorkflowRun run)
    {
        var item = await RecoverWorkItemFromActiveWorkAsync(workflow, workflowRunId, workId, run);
        if (item is null) return null;

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

        var tracked = FindWork(workId, WorkDispatchOwnerKinds.AgentJob, agentJobId) is not null;

        var job = GrainFactory.GetGrain<IAgentJobGrain>(agentJobId);
        var accepted = await job.ReportResultAsync(RunnerId, workId, result);

        if (tracked && accepted.Accepted)
        {
            TryRemoveWork(workId, WorkDispatchOwnerKinds.AgentJob, agentJobId);
            await PersistAsync();
        }

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
        var activeWorks = GetWorks()
            .Select(ProjectActiveWorkFromRunerWork)
            .ToList();

        return new RunnerRuntimeState(
            _status,
            _lastHeartbeat,
            activeWorks);
    }

    private static RunnerActiveWorkItem ProjectActiveWorkFromRunerWork(RunnerWork work)
    {
        return new RunnerActiveWorkItem(
            WorkId: work.WorkId,
            OwnerKind: work.OwnerKind,
            OwnerId: work.OwnerId,
            WorkType: work.WorkType ?? "task",
            Stage: work.Stage,
            Title: work.Title,
            Issue: work.Issue);
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
        var workflowWorks = GetWorks()
            .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow && w.Status == RunnerWorkStatus.Running)
            .ToList();

        if (workflowWorks.Count == 0) return;

        var synthesizedFailure = new WorkResult("failed", "runner-lost");
        foreach (var entry in workflowWorks)
        {
            try
            {
                var workflowRunId = entry.OwnerId;
                await ReportWorkflowResultAsync(workflowRunId, entry.WorkId, synthesizedFailure);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to synthesize failed report for workflow {WorkflowRunId} work {WorkId}",
                    RunnerId,
                    entry.OwnerId,
                    entry.WorkId);
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
        var issue = dispatch.Issue;
        if (issue is null && run.Metadata?.Annotations is { } annotations
            && annotations.TryGetValue("projectId", out var projectId)
            && annotations.TryGetValue("issueId", out var issueId)
            && annotations.TryGetValue("issueNumber", out var numberStr)
            && int.TryParse(numberStr, out var number))
        {
            issue = new WorkIssueRef(projectId, issueId, number);
        }

        AddWork(new RunnerWork
        {
            WorkId = item.Id!,
            OwnerKind = WorkDispatchOwnerKinds.Workflow,
            OwnerId = workflowRunId,
            WorkType = item.WorkType,
            Stage = item.Stage,
            Title = item.Title,
            Issue = issue,
            Status = RunnerWorkStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await PersistAsync();
        return dispatch;
    }

    private async Task<WorkDispatch?> DequeueAssignedAgentJobAsync()
    {
        while (true)
        {
            var pendingWork = GetWorks().FirstOrDefault(w =>
                w.OwnerKind == WorkDispatchOwnerKinds.AgentJob && w.Status == RunnerWorkStatus.Pending);

            if (pendingWork is null)
                return null;

            var agentJobId = pendingWork.OwnerId;
            var workId = pendingWork.WorkId;
            var job = GrainFactory.GetGrain<IAgentJobGrain>(agentJobId);
            if (!await job.IsWorkRunnableAsync(RunnerId, workId))
            {
                _log.LogDebug(
                    "Runner {Id} dropping work {WorkId} for agent-job {AgentJobId}: not runnable",
                    RunnerId, workId, agentJobId);
                TryRemoveWork(workId, WorkDispatchOwnerKinds.AgentJob, agentJobId);
                await PersistAsync();
                continue;
            }

            pendingWork.Status = RunnerWorkStatus.Running;
            pendingWork.StartedAt = DateTimeOffset.UtcNow;
            await PersistAsync();

            return pendingWork.DispatchSnapshot!;
        }
    }

    // ── Persisted works helpers ───────────────────────────────────────────

    private List<RunnerWork> GetWorks()
    {
        _worksState.State ??= new RunnerWorksState();
        _worksState.State.Works ??= [];
        return _worksState.State.Works;
    }

    private void AddWork(RunnerWork work)
    {
        _worksState.State.Works.Add(work);
    }

    private RunnerWork? FindWork(string workId, string ownerKind, string ownerId)
    {
        return GetWorks().FirstOrDefault(w =>
            string.Equals(w.WorkId, workId, StringComparison.Ordinal)
            && string.Equals(w.OwnerKind, ownerKind, StringComparison.Ordinal)
            && string.Equals(w.OwnerId, ownerId, StringComparison.Ordinal));
    }

    private bool TryRemoveWork(string workId, string ownerKind, string ownerId)
    {
        return GetWorks().RemoveAll(w =>
            string.Equals(w.WorkId, workId, StringComparison.Ordinal)
            && string.Equals(w.OwnerKind, ownerKind, StringComparison.Ordinal)
            && string.Equals(w.OwnerId, ownerId, StringComparison.Ordinal)) > 0;
    }

    private async Task ClearWorksAsync(string ownerKind)
    {
        GetWorks().RemoveAll(w => string.Equals(w.OwnerKind, ownerKind, StringComparison.Ordinal));
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        await _worksState.WriteStateAsync();
    }
}

internal sealed record RunnerWorkflowWork(
    WorkItem Item,
    WorkDispatch Dispatch,
    DateTimeOffset PolledAt);
