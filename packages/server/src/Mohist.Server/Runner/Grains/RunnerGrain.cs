using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Runtime;
using LedgerRunnerWork = Mohist.Server.Infrastructure.Data.Runner.RunnerWork;
using LedgerRunnerWorkStatus = Mohist.Server.Infrastructure.Data.Runner.RunnerWorkStatus;

namespace Mohist.Server.Runner.Grains;

/// <summary>
/// Presence, capacity, and closeout for a runner process. Under the
/// reconciliation model (design/workflow/scheduling.md) this grain holds NO
/// workflow work records — the workflow run IS the ledger, and the stateless
/// <see cref="Services.DispatchService"/> computes dispatches per poll. This
/// grain retains only:
/// <list type="bullet">
///   <item><description>presence: lastSeen — poll IS the heartbeat (online/offline).</description></item>
///   <item><description>slots: capacity configuration (control-plane owned).</description></item>
///   <item><description>agent-job push dispatch + ledger (agent-jobs have no run to re-render from; they stay push-based).</description></item>
///   <item><description>closeout: on presence loss, fail active workflow work and the runner's outstanding agent-job works.</description></item>
/// </list>
/// No work-completion wall clock — work liveness is the runner process's
/// poll report; the only server-side timer is presence expiry.
/// </summary>
public class RunnerGrain : Grain, IRunnerGrain, IRemindable
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _pendingBuildGitHash;
    // Agent-job works only. Workflow works live on the run; this grain tracks
    // no workflow records. The push model survives because an AgentJob owns a
    // single work item with no run to re-render from.
    private readonly IPersistentState<RunnerWorksState> _worksState;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _pollAdmitted;
    private DateTimeOffset _lastPresenceAt;
    private IDisposable? _presenceTimer;

    // Authoritative source for dispatch capacity. Loaded from the persisted
    // definition state in OnActivateAsync / RegisterAsync and updated via
    // UpdateAsync (write-through). A value reported by the runner process
    // via register/heartbeat SHALL NOT influence this field.
    private int? _slots;

    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly RunnerDefinitionStore _definitions;
    private readonly RunnerWorkStore _runnerWorks;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunnerGrain> _log;
    private readonly IRunnerGrainAssignmentObserver _assignmentObserver;
    private readonly IRunnerGrainCloseoutObserver _closeoutObserver;
    private readonly IAgentJobWorkCoordinator _agentJobs;

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PresenceCheckInterval = TimeSpan.FromSeconds(10);
    private const string PresenceReminderName = "presence";

    public RunnerGrain(
        WorkflowRunQuerier workflowRuns,
        RunnerDefinitionStore definitions,
        RunnerWorkStore runnerWorks,
        ILogger<RunnerGrain> log,
        TimeProvider timeProvider,
        [PersistentState("runner-works")] IPersistentState<RunnerWorksState> worksState,
        IAgentJobWorkCoordinator agentJobs,
        IRunnerGrainAssignmentObserver? assignmentObserver = null,
        IRunnerGrainCloseoutObserver? closeoutObserver = null)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _runnerWorks = runnerWorks;
        _log = log;
        _timeProvider = timeProvider;
        _worksState = worksState;
        _agentJobs = agentJobs;
        _assignmentObserver = assignmentObserver ?? NoopRunnerGrainAssignmentObserver.Instance;
        _closeoutObserver = closeoutObserver ?? NoopRunnerGrainCloseoutObserver.Instance;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _slots = await _definitions.GetOrInitAsync(RunnerId, ct);
        if (!_worksState.RecordExists)
            await _worksState.ReadStateAsync();
        var state = GetState();
        _info = state.LastKnownInfo;
        if (_info is not null && state.LastKnownActionCatalogJson is not null)
        {
            var catalog = JSON.Deserialize<ActionCatalog>(state.LastKnownActionCatalogJson);
            if (catalog is not null)
                _info = _info with { ActionCatalog = catalog };
        }
        await HydrateOutstandingAgentJobWorksAsync(ct);
    }

    private async Task HydrateOutstandingAgentJobWorksAsync(CancellationToken ct)
    {
        var changed = false;
        var outstanding = await _runnerWorks.ListOutstandingAsync(RunnerId, ct);
        foreach (var work in outstanding)
        {
            if (!string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
                continue; // workflow ledger rows are vestigial; the run owns workflow state now
            if (FindWork(work.WorkId, work.OwnerKind, work.OwnerId) is not null)
                continue;

            AddWork(new RunnerWork
            {
                WorkId = work.WorkId,
                OwnerKind = work.OwnerKind,
                OwnerId = work.OwnerId,
                Status = RunnerWorkStatus.Pending,
                CreatedAt = work.TakenAt,
            });
            changed = true;
        }

        if (changed)
            await PersistAsync();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _presenceTimer?.Dispose();
        _presenceTimer = null;
        return Task.CompletedTask;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // The presence reminder is a no-op tick carrier; the actual presence
        // check is driven by the grain timer registered on register or poll.
        // The reminder exists only so presence-expiry survives silo restart
        // (a grain timer does not). Kept minimal here.
        return Task.CompletedTask;
    }

    public async Task RegisterAsync(RunnerInfo info)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            SetRunnerInfo(InfoForRegister(info));
            _status = RunnerStatus.Online;
            _lastPresenceAt = _timeProvider.GetUtcNow();
            _pendingBuildGitHash = null;
            _slots = await _definitions.GetOrInitAsync(RunnerId);
            await PersistAsync();
            await UpsertRegistryAsync();
            EnsurePresenceTimer();
            _log.LogInformation("Runner {Id} registered from {Host} as global resource with {Slots} persisted workflow slots", info.RunnerId, info.Hostname, _slots);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UnregisterAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _log.LogInformation("Runner {Id} unregistered", RunnerId);
            _status = RunnerStatus.Offline;
            SetRunnerInfo(null);
            await PersistAsync();
            var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            await registry.UnregisterAsync(RunnerId);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await CloseoutLostAsync();
        await ClearWorksAsync(WorkDispatchOwnerKinds.AgentJob);
    }

    public Task HeartbeatAsync() => Task.CompletedTask;

    public async Task HeartbeatRepairAsync(RunnerInfo info)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            SetRunnerInfo(InfoForHeartbeat(info));
            _pendingBuildGitHash = null;
            await PersistAsync();
            if (_status == RunnerStatus.Online)
                await UpsertRegistryAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// A successful poll proves presence. It refreshes the presence timestamp
    /// and restores an expired runner to the registry using the latest info
    /// received from register or heartbeat-repair.
    /// </summary>
    public async Task TouchPresenceAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _lastPresenceAt = _timeProvider.GetUtcNow();
            EnsurePresenceTimer();
            if (_status == RunnerStatus.Online || _info is null)
                return;

            _status = RunnerStatus.Online;
            await UpsertRegistryAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerWorkAssignmentResult> AssignAgentJobAsync(WorkDispatch work)
    {
        if (work.OwnerKind != WorkDispatchOwnerKinds.AgentJob)
            return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work");
        if (string.IsNullOrWhiteSpace(work.AgentJobId) || string.IsNullOrWhiteSpace(work.WorkId))
            return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "invalid-work");

        await _assignmentObserver.AssignmentAdmissionAsync(RunnerId, work);
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_pollAdmitted)
                return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "runner-reconciling");

            if (_status != RunnerStatus.Online || _info is null)
                return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "runner-offline");

            var ownerId = work.AgentJobId!;
            var existing = FindWork(work.WorkId, WorkDispatchOwnerKinds.AgentJob, ownerId);
            if (existing is not null)
                return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned);

            var state = await GetRuntimeStateAsync();
            var activeOwnerCount = state.ActiveWorks
                .Select(item => (item.OwnerKind, item.OwnerId))
                .Distinct()
                .Count();
            if (activeOwnerCount >= MaxWorkflowSlots)
                return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Rejected, "capacity-exhausted");

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
            _log.LogInformation("Runner {Id} assigned work {WorkId} for agent-job {AgentJobId}", RunnerId, work.WorkId, work.AgentJobId);
            return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerPollAdmission> TryBeginPollAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_pollAdmitted)
                return new RunnerPollAdmission(false, 0);

            _pollAdmitted = true;
            return new RunnerPollAdmission(true, MaxWorkflowSlots);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task EndPollAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _pollAdmitted = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }

    }

    public async Task<WorkItem?> TryClaimWorkflowAsync(
        string workflowRunId,
        string? projectId,
        bool assignWorker)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_status != RunnerStatus.Online
                || _info is null
                || !string.Equals(_info.ProjectId, projectId, StringComparison.Ordinal))
            {
                return null;
            }

            var activeWorkflowCount = (await _workflowRuns.FindRunningAssignedToAsync(RunnerId)).Count;
            var activeAgentJobCount = GetWorks()
                .Where(IsActiveAgentJobWork)
                .Select(work => work.OwnerId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (activeWorkflowCount + activeAgentJobCount >= MaxWorkflowSlots)
                return null;

            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            if (assignWorker)
            {
                var assignment = await workflow.AssignWorkerAsync(RunnerId);
                if (assignment.Status != WorkflowAssignmentStatus.Assigned)
                    return null;
            }

            return await workflow.ClaimNextAsync(RunnerId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<AgentJobPollState> ReconcileAgentJobsAsync(List<string> reportedWorkKeys)
    {
        var reported = reportedWorkKeys.ToHashSet(StringComparer.Ordinal);

        // The cross-grain IsWorkRunnableAsync call must NOT happen while
        // _lifecycleGate is held: AgentJobGrain.TryAssignToRunnerAsync calls
        // back into AssignAgentJobAsync (which needs the same gate), and both
        // grains are non-reentrant — holding the gate across the call forms a
        // circular wait. Snapshot the candidate under the gate, release, do
        // the cross-grain check outside, then re-acquire to mutate.
        //
        // _pollAdmitted stays true for the whole poll round (cleared only by
        // DispatchService's finally → EndPollAsync), so AssignAgentJobAsync
        // continues to reject with "runner-reconciling" while the gate is
        // released — the works list cannot be mutated by assignment here.
        while (true)
        {
            ReconcileCandidate? snapshot;
            int activeCount;
            await _lifecycleGate.WaitAsync();
            try
            {
                var activeWorks = GetWorks()
                    .Where(IsActiveAgentJobWork)
                    .ToList();
                activeCount = activeWorks.Count;
                var candidate = activeWorks.FirstOrDefault(work =>
                    !reported.Contains(AgentJobWorkKey(work.OwnerId, work.WorkId)));

                if (candidate is null)
                    return new AgentJobPollState(activeCount, null);

                snapshot = new ReconcileCandidate(
                    candidate.OwnerId,
                    candidate.WorkId);
            }
            finally
            {
                _lifecycleGate.Release();
            }

            var runnable = await _agentJobs.IsWorkRunnableAsync(snapshot.AgentJobId, RunnerId, snapshot.WorkId);

            await _lifecycleGate.WaitAsync();
            try
            {
                // Re-find the work under the gate: it may have been removed by
                // a concurrent path (e.g. HandleTimeoutAsync closeout). If gone,
                // skip to the next candidate.
                var live = FindWork(snapshot.WorkId, WorkDispatchOwnerKinds.AgentJob, snapshot.AgentJobId);
                if (live is null)
                    continue;

                if (!runnable)
                {
                    _log.LogDebug(
                        "Runner {Id} dropping work {WorkId} for agent-job {AgentJobId}: not runnable",
                        RunnerId, snapshot.WorkId, snapshot.AgentJobId);
                    TryRemoveWork(snapshot.WorkId, WorkDispatchOwnerKinds.AgentJob, snapshot.AgentJobId);
                    await PersistAsync();
                    await MarkRunnerWorkTerminalAsync(
                        WorkDispatchOwnerKinds.AgentJob,
                        snapshot.AgentJobId,
                        snapshot.WorkId,
                        LedgerRunnerWorkStatus.Failed,
                        "not-runnable");
                    continue;
                }

                if (live.Status == RunnerWorkStatus.Pending)
                {
                    live.Status = RunnerWorkStatus.Running;
                    live.StartedAt = _timeProvider.GetUtcNow();
                    await PersistAsync();
                }

                return new AgentJobPollState(activeCount, live.DispatchSnapshot);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
    }

    private sealed record ReconcileCandidate(
        string AgentJobId,
        string WorkId);

    public async Task<RunnerWorkReportResult> ReportAgentJobResultAsync(string agentJobId, string workId, WorkResult result)
    {
        if (string.IsNullOrWhiteSpace(agentJobId))
            return new RunnerWorkReportResult(string.Empty, null, false, "missing-agent-job", WorkDispatchOwnerKinds.AgentJob, agentJobId);
        if (string.IsNullOrWhiteSpace(workId))
            return new RunnerWorkReportResult(string.Empty, null, false, "missing-work", WorkDispatchOwnerKinds.AgentJob, agentJobId);

        var accepted = await _agentJobs.ReportAsync(agentJobId, RunnerId, workId, result);

        var tracked = false;
        await _lifecycleGate.WaitAsync();
        try
        {
            tracked = FindWork(workId, WorkDispatchOwnerKinds.AgentJob, agentJobId) is not null;
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
        }
        finally
        {
            _lifecycleGate.Release();
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
        // Active workflow works come from the store (the run owns the state);
        // active agent-job works come from this grain's ledger. Both are
        // projected into the unified RunnerActiveWorkItem shape the read model
        // (RunnerStatusService) consumes.
        var activeWorks = new List<RunnerActiveWorkItem>();
        var workerId = RunnerId;

        foreach (var workflowRunId in await _workflowRuns.FindRunningAssignedToAsync(workerId))
        {
            var run = await _workflowRuns.LoadAsync(workflowRunId);
            if (run is null) continue;
            // Issue metadata lives on the run (annotations), not the work
            // item — project it so the read model keeps the issue reference
            // for active workflow work.
            var issue = IssueFromAnnotations(run);
            var stage = run.CurrentStage();
            var task = stage.RunningTask;
            if (task is not null)
            {
                activeWorks.Add(new RunnerActiveWorkItem(
                    WorkId: task.WorkId ?? task.Id,
                    OwnerKind: WorkDispatchOwnerKinds.Workflow,
                    OwnerId: workflowRunId,
                    WorkType: "task",
                    Stage: stage.Id,
                    Title: task.Title,
                    Issue: issue,
                    TakenAt: task.StartedAt));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(stage.ChecksWorkId))
            {
                activeWorks.Add(new RunnerActiveWorkItem(
                    WorkId: stage.ChecksWorkId,
                    OwnerKind: WorkDispatchOwnerKinds.Workflow,
                    OwnerId: workflowRunId,
                    WorkType: "checks",
                    Stage: stage.Id,
                    Title: "Stage checks",
                    Issue: issue,
                    TakenAt: null));
            }
        }

        foreach (var w in GetWorks().Where(w => w.Status is RunnerWorkStatus.Pending or RunnerWorkStatus.Running
            && w.OwnerKind == WorkDispatchOwnerKinds.AgentJob))
        {
            activeWorks.Add(new RunnerActiveWorkItem(
                WorkId: w.WorkId,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob,
                OwnerId: w.OwnerId,
                WorkType: w.WorkType ?? "agent-job",
                Stage: w.Stage,
                Title: w.Title,
                Issue: w.Issue,
                TakenAt: w.CreatedAt));
        }

        return new RunnerRuntimeState(_status, _lastPresenceAt, activeWorks);
    }

    public async Task UpdateBuildGitHashAsync(string? buildGitHash)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var normalized = string.IsNullOrWhiteSpace(buildGitHash) ? null : buildGitHash.Trim();
            if (_info is null)
            {
                _pendingBuildGitHash = normalized;
                return;
            }

            if (string.Equals(_info.BuildGitHash, normalized, StringComparison.Ordinal))
                return;

            SetRunnerInfo(_info with { BuildGitHash = normalized });
            await PersistAsync();
            _log.LogInformation("Runner {Id} reported buildGitHash {Hash}", RunnerId, normalized ?? "<null>");
            if (_status == RunnerStatus.Online)
                await UpsertRegistryAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private RunnerInfo InfoForRegister(RunnerInfo info)
    {
        return info with
        {
            BuildGitHash = info.BuildGitHash ?? _pendingBuildGitHash,
            RegisteredAt = info.RegisteredAt ?? _timeProvider.GetUtcNow(),
            ActionCatalog = info.ActionCatalog,
        };
    }

    private RunnerInfo InfoForHeartbeat(RunnerInfo info)
    {
        return info with
        {
            BuildGitHash = info.BuildGitHash ?? _pendingBuildGitHash ?? _info?.BuildGitHash,
            RegisteredAt = _info?.RegisteredAt ?? info.RegisteredAt ?? _timeProvider.GetUtcNow(),
            ActionCatalog = info.ActionCatalog,
        };
    }

    private async Task UpsertRegistryAsync()
    {
        if (_info is null)
            return;

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

        await _lifecycleGate.WaitAsync();
        try
        {
            await _definitions.UpdateSlotsAsync(RunnerId, slots);
            _slots = slots;
            _log.LogInformation("Runner {Id} slots updated to {Slots}", RunnerId, slots);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private int MaxWorkflowSlots =>
        _slots ?? RunnerCapacity.DefaultMaxWorkflowSlots;

    private void EnsurePresenceTimer()
    {
        _presenceTimer ??= this.RegisterGrainTimer(
            _ => CheckPresenceAsync(),
            PresenceCheckInterval,
            PresenceCheckInterval);
    }

    private async Task CheckPresenceAsync()
    {
        if (_status == RunnerStatus.Offline) return;

        var elapsed = _timeProvider.GetUtcNow() - _lastPresenceAt;
        if (elapsed > PresenceTimeout)
        {
            _log.LogWarning("Runner {Id} poll presence timeout ({Elapsed}s)", RunnerId, elapsed.TotalSeconds);
            await HandleTimeoutAsync();
        }
    }

    private async Task HandleTimeoutAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_status == RunnerStatus.Offline
                || _timeProvider.GetUtcNow() - _lastPresenceAt <= PresenceTimeout)
            {
                return;
            }

            _status = RunnerStatus.Offline;
            var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            await registry.UnregisterAsync(RunnerId);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await CloseoutLostAsync();
    }

    private async Task CloseoutLostAsync()
    {
        var synthesizedFailure = new WorkResult("failed", "runner-lost");
        var workerId = RunnerId;

        foreach (var workflowRunId in await _workflowRuns.FindRunningAssignedToAsync(workerId))
        {
            try
            {
                var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
                await workflow.FailActiveWorkAsync(workerId, "runner-lost");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to close active workflow work for workflow {WorkflowId}",
                    RunnerId, workflowRunId);
            }
        }

        var agentJobs = GetWorks()
            .Where(w => w.Status is RunnerWorkStatus.Pending or RunnerWorkStatus.Running
                && w.OwnerKind == WorkDispatchOwnerKinds.AgentJob)
            .ToList();
        foreach (var entry in agentJobs)
        {
            try
            {
                if (FindWork(entry.WorkId, entry.OwnerKind, entry.OwnerId) is null)
                    continue;
                await _closeoutObserver.AgentJobCloseoutStartingAsync(RunnerId, entry.OwnerId, entry.WorkId);
                var reportResult = await _agentJobs.ReportAsync(entry.OwnerId, RunnerId, entry.WorkId, synthesizedFailure);
                if (!reportResult.Accepted)
                    await _agentJobs.FailAsync(entry.OwnerId, synthesizedFailure.Message ?? "failed");

                TryRemoveWork(entry.WorkId, entry.OwnerKind, entry.OwnerId);
                await PersistAsync();
                await MarkRunnerWorkTerminalAsync(
                    entry.OwnerKind, entry.OwnerId, entry.WorkId,
                    LedgerRunnerWorkStatus.Failed, "runner-lost");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to synthesize failed report for agent-job {AgentJobId} work {WorkId}",
                    RunnerId, entry.OwnerId, entry.WorkId);
            }
        }
    }

    private List<RunnerWork> GetWorks()
    {
        var state = GetState();
        state.Works ??= [];
        return state.Works;
    }

    private RunnerWorksState GetState() =>
        _worksState.State ??= new RunnerWorksState();

    private void SetRunnerInfo(RunnerInfo? info)
    {
        var retained = info is null
            ? null
            : info with { ActionCatalog = CloneActionCatalog(info.ActionCatalog) };
        _info = retained;
        var state = GetState();
        state.LastKnownInfo = retained;
        state.LastKnownActionCatalogJson = retained?.ActionCatalog is { } catalog
            ? JSON.Serialize(catalog)
            : null;
    }

    private static ActionCatalog? CloneActionCatalog(ActionCatalog? catalog)
    {
        if (catalog is null)
            return null;

        return new ActionCatalog(
            catalog.Actions.Select(action => new ActionCatalogEntry(
                action.Name,
                action.Inputs.Select(input => new ActionCatalogInput(
                    input.Name,
                    [.. input.Types],
                    input.Required,
                    CloneDefault(input.Default),
                    input.Description)).ToArray(),
                action.Outputs.Select(output => new ActionCatalogOutput(output.Name, output.Description)).ToArray(),
                action.Errors.Select(error => new ActionCatalogError(error.Code, error.Description)).ToArray(),
                action.Description)).ToArray(),
            catalog.Tombstones.Select(tombstone => new ActionCatalogTombstone(tombstone.Name, tombstone.Guidance)).ToArray());
    }

    private static System.Text.Json.JsonElement? CloneDefault(System.Text.Json.JsonElement? value)
    {
        if (value is not { } element || element.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            return null;
        return element.Clone();
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

    private static bool IsActiveAgentJobWork(RunnerWork work) =>
        work.OwnerKind == WorkDispatchOwnerKinds.AgentJob
        && work.Status is RunnerWorkStatus.Pending or RunnerWorkStatus.Running;

    private static string AgentJobWorkKey(string agentJobId, string workId) =>
        $"{WorkDispatchOwnerKinds.AgentJob}:{agentJobId}:{workId}";

    private async Task ClearWorksAsync(string ownerKind)
    {
        GetWorks().RemoveAll(w => string.Equals(w.OwnerKind, ownerKind, StringComparison.Ordinal));
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        await _worksState.WriteStateAsync();
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

    /// <summary>
    /// Resolves the issue reference carried on a workflow run's metadata
    /// annotations (projectId / issueNumber). Returns null when the run has no
    /// issue annotation pair. Used to project the issue ref for
    /// active workflow work — issue metadata lives on the run, not the work
    /// item, so without this the read model would lose the issue reference.
    /// </summary>
    private static WorkIssueRef? IssueFromAnnotations(WorkflowRun run)
    {
        if (run.Metadata?.Annotations is not { } annotations) return null;
        if (!annotations.TryGetValue("projectId", out var projectId)
            || !annotations.TryGetValue("issueNumber", out var numberStr)
            || !int.TryParse(numberStr, out var number))
            return null;
        return new WorkIssueRef(projectId, number);
    }
}
