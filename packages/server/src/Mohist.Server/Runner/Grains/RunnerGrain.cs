using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
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
[Reentrant]
public class RunnerGrain : Grain, IRunnerGrain, IRemindable
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _pendingBuildGitHash;
    // Agent-job works only. Workflow works live on the run; this grain tracks
    // no workflow records. The push model survives because an AgentJob owns a
    // single work item with no run to re-render from.
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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private const string PresenceReminderName = "presence";

    public RunnerGrain(
        WorkflowRunQuerier workflowRuns,
        RunnerDefinitionStore definitions,
        RunnerWorkStore runnerWorks,
        ILogger<RunnerGrain> log,
        TimeProvider timeProvider,
        [PersistentState("runner-works")] IPersistentState<RunnerWorksState> worksState)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _runnerWorks = runnerWorks;
        _log = log;
        _timeProvider = timeProvider;
        _worksState = worksState;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _slots = await _definitions.GetOrInitAsync(RunnerId, ct);
        if (!_worksState.RecordExists)
            await _worksState.ReadStateAsync();
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
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        return Task.CompletedTask;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // The presence reminder is a no-op tick carrier; the actual presence
        // check is driven by the grain timer registered in RegisterAsync. The
        // reminder exists only so presence-expiry survives silo restart (a
        // grain timer does not). kept minimal here.
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

        await CloseoutLostAsync();
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

    /// <summary>
    /// Poll IS heartbeat (design §Supervision). Refreshes presence without a
    /// registry write — the registry is written only on state or info change
    /// (register / unregister / heartbeat-repair), not per poll.
    /// </summary>
    public Task TouchPresenceAsync()
    {
        _lastHeartbeat = _timeProvider.GetUtcNow().UtcDateTime;
        return Task.CompletedTask;
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
        _log.LogInformation("Runner {Id} assigned work {WorkId} for agent-job {AgentJobId}", RunnerId, work.WorkId, work.AgentJobId);
        return new RunnerWorkAssignmentResult(RunnerWorkAssignmentStatus.Assigned);
    }

    public async Task<WorkDispatch?> DequeueAssignedAgentJobAsync()
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

        return new RunnerRuntimeState(_status, _lastHeartbeat, activeWorks);
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

    private int MaxWorkflowSlots =>
        _slots ?? RunnerCapacity.DefaultMaxWorkflowSlots;

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
        await CloseoutLostAsync();
        _status = RunnerStatus.Offline;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        await registry.UnregisterAsync(RunnerId);
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
                var job = GrainFactory.GetGrain<IAgentJobGrain>(entry.OwnerId);
                var reportResult = await job.ReportResultAsync(RunnerId, entry.WorkId, synthesizedFailure);
                if (!reportResult.Accepted)
                    await job.FailAsync(synthesizedFailure.Message ?? "failed");

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
    /// annotations (projectId / issueId / issueNumber). Returns null when the
    /// run has no issue annotation triplet. Used to project the issue ref for
    /// active workflow work — issue metadata lives on the run, not the work
    /// item, so without this the read model would lose the issue reference.
    /// </summary>
    private static WorkIssueRef? IssueFromAnnotations(WorkflowRun run)
    {
        if (run.Metadata?.Annotations is not { } annotations) return null;
        if (!annotations.TryGetValue("projectId", out var projectId)
            || !annotations.TryGetValue("issueId", out var issueId)
            || !annotations.TryGetValue("issueNumber", out var numberStr)
            || !int.TryParse(numberStr, out var number))
            return null;
        return new WorkIssueRef(projectId, issueId, number);
    }
}
