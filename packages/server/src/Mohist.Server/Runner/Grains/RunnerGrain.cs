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
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using LedgerRunnerWork = Mohist.Server.Infrastructure.Data.Runner.RunnerWork;
using LedgerRunnerWorkStatus = Mohist.Server.Infrastructure.Data.Runner.RunnerWorkStatus;

namespace Mohist.Server.Runner.Grains;

[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain, IRemindable
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _pendingBuildGitHash;
    private readonly IPersistentState<RunnerWorksState> _worksState;
    private readonly SemaphoreSlim _worksStateWriteGate = new(1, 1);
    private DateTime _lastHeartbeat;
    private IDisposable? _heartbeatTimer;

    // Authoritative source for dispatch capacity. Loaded from the persisted
    // definition state in OnActivateAsync / RegisterAsync and updated via
    // UpdateAsync (write-through). A value reported by the runner process
    // via register/heartbeat SHALL NOT influence this field.
    private int? _slots;

    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly RunnerDefinitionStore _definitions;
    private readonly RunnerWorkStore _runnerWorks;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<RunnerGrain> _log;
    private readonly TimeProvider _timeProvider;
    private readonly WorkflowOptions _workflowOptions;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private const string WorkTimeoutReminderName = "work-timeout";
    private static readonly TimeSpan WorkTimeoutReminderPeriod = TimeSpan.FromMinutes(1);

    public RunnerGrain(
        WorkflowRunQuerier workflowRuns,
        RunnerDefinitionStore definitions,
        RunnerWorkStore runnerWorks,
        WorkflowItemTranslator translator,
        ILogger<RunnerGrain> log,
        TimeProvider timeProvider,
        IOptions<WorkflowOptions> workflowOptions,
        [PersistentState("runner-works")] IPersistentState<RunnerWorksState> worksState)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _runnerWorks = runnerWorks;
        _translator = translator;
        _log = log;
        _timeProvider = timeProvider;
        _workflowOptions = workflowOptions.Value;
        _worksState = worksState;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _slots = await _definitions.GetOrInitAsync(RunnerId, ct);
        if (!_worksState.RecordExists)
            await _worksState.ReadStateAsync();
        await HydrateOutstandingWorksAsync(ct);
    }

    private async Task HydrateOutstandingWorksAsync(CancellationToken ct)
    {
        var changed = false;
        var outstanding = await _runnerWorks.ListOutstandingAsync(RunnerId, ct);
        foreach (var work in outstanding)
        {
            if (FindWork(work.WorkId, work.OwnerKind, work.OwnerId) is not null)
                continue;

            AddWork(new RunnerWork
            {
                WorkId = work.WorkId,
                OwnerKind = work.OwnerKind,
                OwnerId = work.OwnerId,
                Status = string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
                    ? RunnerWorkStatus.Pending
                    : RunnerWorkStatus.Running,
                CreatedAt = work.TakenAt,
            });
            changed = true;
        }

        if (changed)
            await PersistAsync();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        return Task.CompletedTask;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, WorkTimeoutReminderName, StringComparison.Ordinal))
            return Task.CompletedTask;

        return CheckWorkTimeoutsAsync();
    }

    public async Task CheckWorkTimeoutsAsync()
    {
        var timeout = _workflowOptions.WorkCompletionTimeout;
        if (timeout <= TimeSpan.Zero)
            return;

        var snapshot = GetWorks()
            .Where(w => w.Status is RunnerWorkStatus.Pending or RunnerWorkStatus.Running)
            .Select(w => new RunnerWork
            {
                WorkId = w.WorkId,
                OwnerKind = w.OwnerKind,
                OwnerId = w.OwnerId,
                WorkType = w.WorkType,
                Stage = w.Stage,
                Title = w.Title,
                Issue = w.Issue,
                Status = w.Status,
                CreatedAt = w.CreatedAt,
                StartedAt = w.StartedAt,
                DispatchSnapshot = w.DispatchSnapshot,
            })
            .ToList();

        if (snapshot.Count == 0)
        {
            await MaybeUnregisterWorkTimeoutReminderAsync();
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var synthesizedFailure = new WorkResult("failed", "timeout");
        foreach (var work in snapshot)
        {
            try
            {
                if (now - work.CreatedAt <= timeout)
                    continue;

                if (!await ReconfirmOutstandingAsync(work))
                    continue;

                await SynthesizeFailureAsync(work, synthesizedFailure);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to synthesize timeout for {OwnerKind} {OwnerId} work {WorkId}",
                    RunnerId,
                    work.OwnerKind,
                    work.OwnerId,
                    work.WorkId);
            }
        }

        if (!HasOutstandingWork())
            await MaybeUnregisterWorkTimeoutReminderAsync();
    }

    public async Task RegisterAsync(RunnerInfo info)
    {
        var effectiveHash = info.BuildGitHash ?? _pendingBuildGitHash;
        _info = info with
        {
            BuildGitHash = effectiveHash,
        };
        _status = RunnerStatus.Online;
        _lastHeartbeat = _timeProvider.GetUtcNow().UtcDateTime;
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
        await RemoveStaleWorkflowWorksAsync();

        var persistedCount = GetWorks()
            .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow && w.Status == RunnerWorkStatus.Running)
            .Select(w => w.OwnerId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (persistedCount > 0)
            return persistedCount;

        // Issue-318 D4: under the new state machine the previous
        // FindAssignedToAsync + GetCurrentWorkIdAsync fan-out collapsed to
        // zero (Ready excludes in-flight work). The dispatch-capacity gate
        // now reads status = Running AND AssignedRunnerId = <runner>
        // directly via the STORED Status computed column (no grain
        // round-trip, no State-deserialize).
        return await _workflowRuns.CountRunningAssignedToAsync(RunnerId);
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

        var takenAt = _timeProvider.GetUtcNow();
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
            CreatedAt = takenAt,
            DispatchSnapshot = work,
        });
        await PersistAsync();
        await _runnerWorks.InsertOutstandingAsync(new LedgerRunnerWork(
            RunnerId,
            work.OwnerKind,
            ownerId,
            work.WorkId,
            takenAt,
            LedgerRunnerWorkStatus.Outstanding));
        await EnsureWorkTimeoutReminderAsync();
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
            var item = await RecoverWorkItemFromActiveWorkAsync(workflow, workflowRunId, workId, run)
                ?? RecoverWorkItemFromRun(workId, run);
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
        {
            if (tracked)
            {
                TryRemoveWork(workId, WorkDispatchOwnerKinds.Workflow, workflowRunId);
                await PersistAsync();
                if (await IsRunnerWorkOutstandingAsync(WorkDispatchOwnerKinds.Workflow, workflowRunId, workId))
                {
                    var (staleTerminalStatus, staleTerminalReason) = IsSyntheticFailure(result)
                        ? ResolveTerminalStatus(result)
                        : (LedgerRunnerWorkStatus.Failed, "stale-work");
                    await MarkRunnerWorkTerminalAsync(
                        WorkDispatchOwnerKinds.Workflow,
                        workflowRunId,
                        workId,
                        staleTerminalStatus,
                        staleTerminalReason);
                }
                return new RunnerWorkReportResult(workflowRunId, null, true, "stale-work", WorkDispatchOwnerKinds.Workflow, workflowRunId);
            }

            return new RunnerWorkReportResult(workflowRunId, null, false, "untracked", WorkDispatchOwnerKinds.Workflow, workflowRunId);
        }

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

        var (terminalStatus, reason) = ResolveTerminalStatus(result);
        await MarkRunnerWorkTerminalAsync(
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId,
            workId,
            terminalStatus,
            reason);

        if (!HasOutstandingWork())
            await MaybeUnregisterWorkTimeoutReminderAsync();

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

    private static WorkItem? RecoverWorkItemFromRun(string workId, WorkflowRun run)
    {
        var stage = run.CurrentStage();
        var task = stage.Tasks.FirstOrDefault(t =>
            t.Status == TaskRunStatus.Running
            && string.Equals(t.WorkId ?? t.Id, workId, StringComparison.Ordinal));
        if (task is not null)
        {
            return WorkItem.Task(
                stage.Id,
                task.WorkId ?? task.Id,
                task.Title,
                task.Uses,
                task.WithInput,
                task.Artifacts,
                task.SetVars,
                task.Recovery);
        }

        if (string.Equals(stage.ChecksWorkId, workId, StringComparison.Ordinal))
        {
            var pendingChecks = stage.Checks
                .Where(c => c.Status is StageCheckStatus.Pending or StageCheckStatus.Running)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();
            return WorkItem.Checks(stage.Id, workId, pendingChecks);
        }

        return null;
    }

    private async Task<RunnerWorkflowWork?> RecoverActiveWorkflowWorkAsync(
        IWorkflowGrain workflow,
        string workflowRunId,
        string workId,
        WorkflowRun run)
    {
        var item = await RecoverWorkItemFromActiveWorkAsync(workflow, workflowRunId, workId, run);
        item ??= RecoverWorkItemFromRun(workId, run);
        if (item is null) return null;

        var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, RunnerId);
        var ledger = await _runnerWorks.FindAsync(
            RunnerId,
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId,
            workId,
            CancellationToken.None);
        var takenAt = ledger?.TakenAt
            ?? FindWork(workId, WorkDispatchOwnerKinds.Workflow, workflowRunId)?.CreatedAt
            ?? _timeProvider.GetUtcNow();
        return new RunnerWorkflowWork(item, dispatch, takenAt);
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
            var (terminalStatus, terminalReason) = ResolveTerminalStatus(result);
            await MarkRunnerWorkTerminalAsync(
                WorkDispatchOwnerKinds.AgentJob,
                agentJobId,
                workId,
                terminalStatus,
                terminalReason);
        }

        if (!HasOutstandingWork())
            await MaybeUnregisterWorkTimeoutReminderAsync();

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
        await RemoveStaleWorkflowWorksAsync();

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
            WorkType: work.WorkType ?? (work.OwnerKind == WorkDispatchOwnerKinds.AgentJob ? "agent-job" : "task"),
            Stage: work.Stage,
            Title: work.Title,
            Issue: work.Issue,
            TakenAt: work.CreatedAt);
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

        var elapsed = _timeProvider.GetUtcNow().UtcDateTime - _lastHeartbeat;
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
        var activeWorks = GetWorks()
            .Where(w => w.Status is RunnerWorkStatus.Pending or RunnerWorkStatus.Running)
            .ToList();

        if (activeWorks.Count == 0) return;

        var synthesizedFailure = new WorkResult("failed", "runner-lost");
        foreach (var entry in activeWorks)
        {
            try
            {
                if (!await ReconfirmOutstandingAsync(entry))
                    continue;

                await SynthesizeFailureAsync(entry, synthesizedFailure);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to synthesize failed report for {OwnerKind} {OwnerId} work {WorkId}",
                    RunnerId,
                    entry.OwnerKind,
                    entry.OwnerId,
                    entry.WorkId);
            }
        }
    }

    private async Task TouchPresenceAsync()
    {
        _lastHeartbeat = _timeProvider.GetUtcNow().UtcDateTime;
        if (_info is null) return;

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.RegisterAsync(_info);
    }

    private async Task<WorkDispatch?> PollAssignedOrAssignableWorkflowAsync()
    {
        await RemoveStaleWorkflowWorksAsync();

        // Issue-318 D4: FindAssignedToAsync now filters at the DB layer on
        // status = Ready AND AssignedRunnerId = <runner>. Ready excludes
        // in-flight work, so every surfaced row is directly pickup-able —
        // the previous GetCurrentWorkIdAsync busy pre-check
        // (~104 grain calls/s) is gone. PollWorkAsync itself still
        // short-circuits null when the run's persisted state disagrees,
        // so the dropped pre-check is safe.
        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(RunnerId))
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
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
        // Offer: PollWorkAsync returns pending work WITHOUT transitioning it
        // to Running. The task/check stays Pending until we confirm the claim.
        var item = await workflow.PollWorkAsync(RunnerId);
        if (item is null) return null;

        var workId = item.Id ?? throw new InvalidOperationException($"Workflow '{workflowRunId}' returned a work item without an id");
        var takenAt = _timeProvider.GetUtcNow();

        // Load the run snapshot once, up front — it's needed to build the
        // dispatch regardless of claim outcome, and loading it before the
        // claim avoids the situation where ClaimAsync succeeds (task marked
        // Running on the workflow) but the run snapshot is missing, which
        // would leave the runner unable to build a dispatch for work it
        // already claimed.
        var run = await _workflowRuns.LoadAsync(workflowRunId);
        if (run is null) return null;

        // Claim locally (durable): register the work in grain state + ledger
        // BEFORE telling the workflow. This ordering guarantees that if the
        // workflow subsequently marks the work Running, a runner record for
        // it already exists — so "Running ⟺ durably claimed" holds.
        AddWork(new RunnerWork
        {
            WorkId = workId,
            OwnerKind = WorkDispatchOwnerKinds.Workflow,
            OwnerId = workflowRunId,
            WorkType = item.WorkType,
            Stage = item.Stage,
            Title = item.Title,
            Status = RunnerWorkStatus.Running,
            CreatedAt = takenAt,
        });
        await PersistAsync();
        await _runnerWorks.InsertOutstandingAsync(new LedgerRunnerWork(
            RunnerId,
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId,
            workId,
            takenAt,
            LedgerRunnerWorkStatus.Outstanding));
        await EnsureWorkTimeoutReminderAsync();

        // Confirm the claim with the workflow. Only now does the task/check
        // transition to Running. If confirmation fails (the offer was
        // overtaken — another runner claimed it, or the stage advanced),
        // roll back the local claim so we are not left tracking work the
        // workflow does not consider ours.
        var resolvedWorkId = await workflow.ClaimAsync(RunnerId, workId);
        if (resolvedWorkId is null)
        {
            await RollbackClaimAsync(workflowRunId, workId, "claim-rejected");
            return null;
        }

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

        // Backfill the issue ref on the tracked work now that the dispatch
        // (which carries the resolved issue) has been built. The claim was
        // registered without it because the work-item offer does not carry
        // issue metadata; it is purely informational for status projection.
        if (issue is not null)
        {
            var tracked = FindWork(workId, WorkDispatchOwnerKinds.Workflow, workflowRunId);
            if (tracked is not null && tracked.Issue is null)
            {
                tracked.Issue = issue;
                await PersistAsync();
            }
        }

        return dispatch;
    }

    private async Task RollbackClaimAsync(string workflowRunId, string workId, string reason)
    {
        TryRemoveWork(workId, WorkDispatchOwnerKinds.Workflow, workflowRunId);
        await PersistAsync();
        await MarkRunnerWorkTerminalAsync(
            WorkDispatchOwnerKinds.Workflow,
            workflowRunId,
            workId,
            LedgerRunnerWorkStatus.Failed,
            reason);
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
                await MarkRunnerWorkTerminalAsync(
                    WorkDispatchOwnerKinds.AgentJob,
                    agentJobId,
                    workId,
                    LedgerRunnerWorkStatus.Failed,
                    "not-runnable");
                continue;
            }

            pendingWork.Status = RunnerWorkStatus.Running;
            pendingWork.StartedAt = _timeProvider.GetUtcNow();
            await PersistAsync();

            return pendingWork.DispatchSnapshot!;
        }
    }

    private async Task RemoveStaleWorkflowWorksAsync()
    {
        var stale = new List<RunnerWork>();
        foreach (var work in GetWorks()
            .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow && w.Status == RunnerWorkStatus.Running)
            .ToList())
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(work.OwnerId);
            var active = await workflow.GetActiveWorkAsync(work.WorkId);
            if (active is null || !string.Equals(active.WorkId, work.WorkId, StringComparison.Ordinal))
                stale.Add(work);
        }

        if (stale.Count == 0)
            return;

        foreach (var work in stale)
        {
            TryRemoveWork(work.WorkId, work.OwnerKind, work.OwnerId);
            await MarkRunnerWorkTerminalAsync(
                work.OwnerKind,
                work.OwnerId,
                work.WorkId,
                LedgerRunnerWorkStatus.Failed,
                "stale-work");
            _log.LogInformation(
                "Runner {RunnerId} removed stale workflow work {WorkId} for {WorkflowRunId}",
                RunnerId,
                work.WorkId,
                work.OwnerId);
        }

        await PersistAsync();
    }

    private List<RunnerWork> GetWorks()
    {
        _worksState.State ??= new RunnerWorksState();
        _worksState.State.Works ??= [];
        return _worksState.State.Works;
    }

    private void AddWork(RunnerWork work)
    {
        GetWorks().Add(work);
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
        await _worksStateWriteGate.WaitAsync();
        try
        {
            await _worksState.WriteStateAsync();
        }
        finally
        {
            _worksStateWriteGate.Release();
        }
    }

    private bool HasOutstandingWork()
    {
        return GetWorks().Any(w => w.Status is RunnerWorkStatus.Pending or RunnerWorkStatus.Running);
    }

    private async Task EnsureWorkTimeoutReminderAsync()
    {
        try
        {
            if (await this.GetReminder(WorkTimeoutReminderName) is not null)
                return;

            await this.RegisterOrUpdateReminder(
                WorkTimeoutReminderName,
                WorkTimeoutReminderPeriod,
                WorkTimeoutReminderPeriod);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Runner {RunnerId} failed to register work-timeout reminder", RunnerId);
        }
    }

    private async Task MaybeUnregisterWorkTimeoutReminderAsync()
    {
        try
        {
            if (HasOutstandingWork())
                return;

            var reminder = await this.GetReminder(WorkTimeoutReminderName);
            if (reminder is null)
                return;

            if (HasOutstandingWork())
                return;

            await this.UnregisterReminder(reminder);

            if (HasOutstandingWork())
                await EnsureWorkTimeoutReminderAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Runner {RunnerId} failed to unregister work-timeout reminder", RunnerId);
            if (HasOutstandingWork())
                await EnsureWorkTimeoutReminderAsync();
        }
    }

    private async Task<bool> ReconfirmOutstandingAsync(RunnerWork work)
    {
        if (FindWork(work.WorkId, work.OwnerKind, work.OwnerId) is null)
            return false;

        var ledger = await _runnerWorks.FindAsync(
            RunnerId,
            work.OwnerKind,
            work.OwnerId,
            work.WorkId);

        if (ledger is null || ledger.Status != LedgerRunnerWorkStatus.Outstanding)
        {
            TryRemoveWork(work.WorkId, work.OwnerKind, work.OwnerId);
            await PersistAsync();
            return false;
        }

        return true;
    }

    private async Task SynthesizeFailureAsync(RunnerWork work, WorkResult result)
    {
        if (string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal))
        {
            await ReportWorkflowResultAsync(work.OwnerId, work.WorkId, result);
            return;
        }

        if (string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            await SynthesizeAgentJobFailureAsync(work, result);
    }

    private async Task SynthesizeAgentJobFailureAsync(RunnerWork work, WorkResult result)
    {
        if (FindWork(work.WorkId, work.OwnerKind, work.OwnerId) is null)
            return;

        var job = GrainFactory.GetGrain<IAgentJobGrain>(work.OwnerId);
        var reportResult = await job.ReportResultAsync(RunnerId, work.WorkId, result);

        if (!reportResult.Accepted)
            await job.FailAsync(result.Message ?? "failed");

        TryRemoveWork(work.WorkId, work.OwnerKind, work.OwnerId);
        await PersistAsync();
        var (terminalStatus, terminalReason) = ResolveTerminalStatus(result);
        await MarkRunnerWorkTerminalAsync(
            work.OwnerKind,
            work.OwnerId,
            work.WorkId,
            terminalStatus,
            terminalReason);
    }

    private async Task MarkRunnerWorkTerminalAsync(
        string ownerKind,
        string ownerId,
        string workId,
        LedgerRunnerWorkStatus status,
        string? reason)
    {
        await _runnerWorks.TryMarkTerminalAsync(
            RunnerId,
            ownerKind,
            ownerId,
            workId,
            status,
            reason,
            _timeProvider.GetUtcNow());
    }

    private async Task<bool> IsRunnerWorkOutstandingAsync(string ownerKind, string ownerId, string workId)
    {
        var ledger = await _runnerWorks.FindAsync(RunnerId, ownerKind, ownerId, workId);
        return ledger?.Status == LedgerRunnerWorkStatus.Outstanding;
    }

    private static (LedgerRunnerWorkStatus Status, string? Reason) ResolveTerminalStatus(WorkResult result)
    {
        var isSuccess = string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);

        return isSuccess
            ? (LedgerRunnerWorkStatus.Completed, null)
            : (LedgerRunnerWorkStatus.Failed, string.IsNullOrWhiteSpace(result.Message) ? result.Status : result.Message);
    }

    private static bool IsSyntheticFailure(WorkResult result) =>
        string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(result.Message, "timeout", StringComparison.Ordinal)
            || string.Equals(result.Message, "runner-lost", StringComparison.Ordinal));
}

internal sealed record RunnerWorkflowWork(
    WorkItem Item,
    WorkDispatch Dispatch,
    DateTimeOffset PolledAt);
