using System.Globalization;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
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

namespace Mohist.Server.Runner.Grains;

/// <summary>
/// Presence, capacity, and closeout for a runner process. Under the
/// reconciliation model this grain holds NO
/// workflow work records — the workflow run IS the ledger, and the stateless
/// <see cref="Services.DispatchService"/> computes dispatches per poll. This
/// grain retains only:
/// <list type="bullet">
///   <item><description>presence: lastSeen — poll IS the heartbeat (online/offline).</description></item>
///   <item><description>slots: capacity configuration (control-plane owned).</description></item>
///   <item><description>closeout: on presence loss, record recoverable workflow interruptions while retaining active AgentJob ledgers for reconnect redelivery.</description></item>
/// </list>
/// No work-completion wall clock — work liveness is the runner process's
/// poll report; the only server-side timer is presence expiry.
/// </summary>
public partial class RunnerGrain : Grain, IRunnerGrain, IRemindable
{
    private RunnerStatus _status = RunnerStatus.Offline;
    private RunnerInfo? _info;
    private string? _pendingBuildGitHash;
    private PendingRuntimeIdentity? _pendingRuntimeIdentity;
    private readonly IPersistentState<RunnerState> _state;
    private readonly IPersistentState<LegacyRunnerRegistrationState> _legacyState;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Guid? _pollAdmissionToken;
    private bool _draining;
    private DateTimeOffset _lastPresenceAt;
    private IDisposable? _presenceTimer;
    private string? _readinessConnectionGeneration;
    private readonly Dictionary<string, RuntimeReadinessWitness> _runtimeReadiness = new(StringComparer.OrdinalIgnoreCase);

    // Authoritative source for dispatch capacity. Loaded from the persisted
    // definition state in OnActivateAsync / RegisterAsync and updated via
    // UpdateAsync (write-through). A value reported by the runner process
    // via register/heartbeat SHALL NOT influence this field.
    private int? _slots;

    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly RunnerDefinitionStore _definitions;
    private readonly IAgentJobStore _agentJobStore;
    private readonly AgentJobOptions _agentJobOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunnerGrain> _log;

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PresenceCheckInterval = TimeSpan.FromSeconds(10);
    private const string PresenceReminderName = "presence";

    public RunnerGrain(
        WorkflowRunQuerier workflowRuns,
        RunnerDefinitionStore definitions,
        IAgentJobStore agentJobStore,
        IOptions<AgentJobOptions> agentJobOptions,
        ILogger<RunnerGrain> log,
        TimeProvider timeProvider,
        [PersistentState("runner")] IPersistentState<RunnerState> state,
        [PersistentState("runner-works")] IPersistentState<LegacyRunnerRegistrationState> legacyState)
    {
        _workflowRuns = workflowRuns;
        _definitions = definitions;
        _agentJobStore = agentJobStore;
        _agentJobOptions = agentJobOptions.Value;
        ValidateRunnerLossRecoveryTimeout(_agentJobOptions.RunnerLossRecoveryTimeout);
        _log = log;
        _timeProvider = timeProvider;
        _state = state;
        _legacyState = legacyState;
    }

    private string RunnerId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _slots = await _definitions.GetOrInitAsync(RunnerId, ct);
        if (!_state.RecordExists)
            await _state.ReadStateAsync();
        if (!_state.RecordExists)
        {
            // The legacy runner-works state predates the RunnerState split and
            // may carry a $type that no longer resolves (its state type was
            // renamed from RunnerWorksState to LegacyRunnerRegistrationState).
            // Its only purpose is a one-time migration of cached registration
            // facts, which the runner re-supplies on connect, so a read failure
            // is non-fatal: skip the migration and let the runner register
            // fresh into the current runner storage.
            if (!_legacyState.RecordExists)
            {
                try
                {
                    await _legacyState.ReadStateAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Legacy runner-works state for runner {RunnerId} could not be read; skipping one-time migration.", RunnerId);
                }
            }
            if (_legacyState.RecordExists && _legacyState.State is not null)
            {
                _state.State = new RunnerState
                {
                    LastKnownInfo = _legacyState.State.LastKnownInfo,
                    LastKnownActionCatalogJson = _legacyState.State.LastKnownActionCatalogJson,
                };
                await _state.WriteStateAsync();
            }
        }
        var state = _state.State ??= new RunnerState();
        _info = state.LastKnownInfo;
        _draining = !string.IsNullOrWhiteSpace(state.UpdateInterruptFence?.PendingId);
        if (_info is not null && state.LastKnownActionCatalogJson is not null)
        {
            var catalog = JSON.Deserialize<ActionCatalog>(state.LastKnownActionCatalogJson);
            if (catalog is not null)
                _info = _info with { ActionCatalog = catalog };
        }
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _presenceTimer?.Dispose();
        _presenceTimer = null;
        return Task.CompletedTask;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // Presence reminder is a no-op tick carrier; the actual check runs on
        // the register/poll grain timer. The reminder exists only so
        // presence-expiry survives silo restart (a grain timer does not).
        return Task.CompletedTask;
    }

    public async Task RegisterAsync(RunnerInfo info)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _pollAdmissionToken = null;
            SetRunnerInfo(InfoForRegister(info));
            _status = RunnerStatus.Online;
            // Registration is the update handoff completion boundary. Persist
            // it before reopening admission so an activation cannot silently
            // erase a confirmed fence while the old process is still active.
            var updateInterruptFence = UpdateInterruptFence();
            _readinessConnectionGeneration = null;
            _runtimeReadiness.Clear();
            _lastPresenceAt = _timeProvider.GetUtcNow();
            _pendingBuildGitHash = null;
            _pendingRuntimeIdentity = null;
            _slots = await _definitions.GetOrInitAsync(RunnerId);
            await PersistUpdateInterruptFenceAsync(
                updateInterruptFence,
                pendingId: null,
                lastCancelledId: null);
            _draining = false;
            await UpsertRegistryAsync();
            EnsurePresenceTimer();
            _log.LogInformation("Runner {Id} registered from {Host} as global resource with {Slots} persisted execution slots", info.RunnerId, info.Hostname, _slots);
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
            _pollAdmissionToken = null;
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
    }

    public Task HeartbeatAsync() => TouchPresenceAsync();

    public async Task HeartbeatRepairAsync(RunnerInfo info)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            SetRunnerInfo(InfoForHeartbeat(info));
            _pendingBuildGitHash = null;
            await PersistAsync();
            await TouchPresenceUnderGateAsync(refreshRegistry: true);
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
            await TouchPresenceUnderGateAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task TouchPresenceUnderGateAsync(bool refreshRegistry = false)
    {
        _lastPresenceAt = _timeProvider.GetUtcNow();
        EnsurePresenceTimer();
        if (_status == RunnerStatus.Online)
        {
            if (refreshRegistry)
                await UpsertRegistryAsync();
            return;
        }
        if (_info is null)
            return;

        _status = RunnerStatus.Online;
        await UpsertRegistryAsync();
    }

    public async Task<RunnerPollAdmission> TryBeginPollAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_draining || _pollAdmissionToken is not null)
                return new RunnerPollAdmission(false, 0);

            var admissionToken = Guid.NewGuid();
            _pollAdmissionToken = admissionToken;
            return new RunnerPollAdmission(true, MaxWorkflowSlots, admissionToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task EndPollAsync(Guid admissionToken)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (admissionToken != Guid.Empty && _pollAdmissionToken == admissionToken)
                _pollAdmissionToken = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }

    }

    public async Task<RunnerRuntimeReadinessSnapshot> ObserveRuntimeReadinessAsync(
        string? connectionGeneration,
        List<RuntimeReadinessWitness> witnesses)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_status != RunnerStatus.Online
                || _info is null
                || string.IsNullOrWhiteSpace(connectionGeneration))
                return RunnerRuntimeReadinessSnapshot.Empty;

            var normalizedConnectionGeneration = connectionGeneration.Trim();
            if (_info.ConnectionGeneration is { } registered
                && !string.Equals(registered, normalizedConnectionGeneration, StringComparison.Ordinal))
                return new RunnerRuntimeReadinessSnapshot(normalizedConnectionGeneration, []);

            if (!string.Equals(_readinessConnectionGeneration, normalizedConnectionGeneration, StringComparison.Ordinal))
            {
                _readinessConnectionGeneration = normalizedConnectionGeneration;
                _runtimeReadiness.Clear();
            }

            foreach (var witness in witnesses ?? [])
            {
                var runtime = witness.Runtime?.Trim();
                if (string.IsNullOrWhiteSpace(runtime) || witness.Generation is not > 0)
                    continue;

                if (_runtimeReadiness.TryGetValue(runtime, out var previous)
                    && previous.Generation is { } previousGeneration
                    && witness.Generation is { } incomingGeneration
                    && previousGeneration > incomingGeneration)
                    continue;

                _runtimeReadiness[runtime] = witness with { Runtime = runtime };
            }

            return new RunnerRuntimeReadinessSnapshot(
                _readinessConnectionGeneration,
                _runtimeReadiness.Values.ToList());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task BeginDrainAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _draining = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerRuntimeState?> BeginUpdateInterruptAsync(string? updateInterruptId = null)
    {
        var requestedId = NormalizeUpdateInterruptId(updateInterruptId);
        if (!string.IsNullOrEmpty(updateInterruptId) && requestedId is null)
            throw new ArgumentException("update interrupt id must be a UUID", nameof(updateInterruptId));

        await _lifecycleGate.WaitAsync();
        try
        {
            if (_status != RunnerStatus.Online || _info is null)
                return null;

            var fence = UpdateInterruptFence();
            if (string.IsNullOrWhiteSpace(fence.PendingId))
            {
                // A delayed duplicate begin must not recreate a fence that a
                // matching rollback has already durably released.
                if (requestedId is not null
                    && string.Equals(fence.LastCancelledId, requestedId, StringComparison.Ordinal))
                {
                    return await BuildRuntimeStateAsync();
                }

                await PersistUpdateInterruptFenceAsync(
                    fence,
                    requestedId ?? Guid.NewGuid().ToString("N"),
                    lastCancelledId: null);
            }

            _draining = true;
            return await BuildRuntimeStateAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerUpdateInterruptCancelResult> CancelUpdateInterruptAsync(string updateInterruptId)
    {
        var normalizedId = NormalizeUpdateInterruptId(updateInterruptId)
            ?? throw new ArgumentException("update interrupt id must be a UUID", nameof(updateInterruptId));

        await _lifecycleGate.WaitAsync();
        try
        {
            var fence = UpdateInterruptFence();
            if (string.Equals(fence.PendingId, normalizedId, StringComparison.Ordinal))
            {
                await PersistUpdateInterruptFenceAsync(
                    fence,
                    pendingId: null,
                    lastCancelledId: normalizedId);
                _draining = false;
                return new RunnerUpdateInterruptCancelResult(normalizedId, RunnerUpdateInterruptCancelStatus.Cancelled);
            }

            if (string.IsNullOrWhiteSpace(fence.PendingId)
                && string.Equals(fence.LastCancelledId, normalizedId, StringComparison.Ordinal))
            {
                return new RunnerUpdateInterruptCancelResult(normalizedId, RunnerUpdateInterruptCancelStatus.AlreadyCancelled);
            }

            return new RunnerUpdateInterruptCancelResult(normalizedId, RunnerUpdateInterruptCancelStatus.Superseded);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CancelDrainAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            // Generic draining is intentionally not allowed to undo a
            // persisted update fence. Only the matching update lease or a
            // successful replacement registration can reopen that boundary.
            if (!string.IsNullOrWhiteSpace(_state.State?.UpdateInterruptFence?.PendingId))
                return;
            _draining = false;
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
            if (_draining
                || _status != RunnerStatus.Online
                || _info is null
                || !string.Equals(_info.ProjectId, projectId, StringComparison.Ordinal))
            {
                return null;
            }

            var activeWorkflowCount = (await _workflowRuns.FindRunningAssignedToAsync(RunnerId))
                .Count(runId => !string.Equals(runId, workflowRunId, StringComparison.Ordinal));
            var activeAgentJobCount = (await _agentJobStore.ListRunningForRunnerAsync(RunnerId))
                .Select(work => work.JobKey)
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

    public async Task<ClaimResult?> TryClaimAgentJobAsync(
        string agentJobId,
        string? projectId,
        CapabilityClaimExpectation? expectation = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_draining
                || _status != RunnerStatus.Online
                || _info is null
                || !string.Equals(_info.ProjectId, projectId, StringComparison.Ordinal))
            {
                return null;
            }

            if (expectation is not null
                && (!string.Equals(expectation.OwnerId, agentJobId, StringComparison.Ordinal)
                    || !RunnerCapabilityGate.Matches(_info, _readinessConnectionGeneration, _runtimeReadiness, expectation)))
                return null;

            var activeWorkflowCount = await _workflowRuns.CountRunningAssignedToAsync(RunnerId);
            var activeAgentJobCount = (await _agentJobStore.ListRunningForRunnerAsync(RunnerId))
                .Select(record => record.JobKey)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (activeWorkflowCount + activeAgentJobCount >= MaxWorkflowSlots)
                return null;

            var job = GrainFactory.GetGrain<IAgentJobGrain>(agentJobId);
            return expectation is null
                ? await job.ClaimNextAsync(RunnerId)
                : await job.ClaimNextAsync(RunnerId, expectation);
        }
        catch (AgentJobLedgerConflictException)
        {
            return null;
        }
        catch (AgentJobLedgerReconstructionException ex)
        {
            _log.LogWarning(ex,
                "Runner {RunnerId} rejected malformed AgentJob dispatch for {AgentJobId}",
                RunnerId,
                agentJobId);
            await GrainFactory.GetGrain<IAgentJobGrain>(agentJobId).FailAsync("invalid-dispatch");
            return null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerRuntimeState> GetRuntimeStateAsync()
    {
        return await BuildRuntimeStateAsync();
    }

    private async Task<RunnerRuntimeState> BuildRuntimeStateAsync()
    {
        // Both owner ledgers are projected into the unified runtime view.
        var activeWorks = new List<RunnerActiveWorkItem>();
        var workerId = RunnerId;

        foreach (var workflowRunId in await _workflowRuns.FindRunningAssignedToAsync(workerId))
        {
            var run = await _workflowRuns.LoadAsync(workflowRunId);
            if (run is null) continue;
            // Issue metadata lives on the run (annotations), not the work
            // item — project it so the read model keeps the issue reference
            // for active workflow work.
            var issue = IssueFromRun(run);
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
                    TakenAt: task.StartedAt,
                    TaskRunId: task.Id,
                    IsAgentWork: task.AgentResultSettlement is not null));
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

        foreach (var w in await _agentJobStore.ListRunningForRunnerAsync(workerId))
        {
            activeWorks.Add(new RunnerActiveWorkItem(
                WorkId: w.WorkId!,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob,
                OwnerId: w.JobKey,
                WorkType: w.WorkType ?? "agent-job",
                Stage: w.Stage,
                Title: w.Title,
                Issue: w.IssueProjectId is not null && w.IssueNumber is not null
                    ? new WorkIssueRef(w.IssueProjectId, w.IssueNumber.Value)
                    : null,
                TakenAt: w.RunningSince));
        }

        return new RunnerRuntimeState(
            _status,
            _lastPresenceAt,
            activeWorks,
            _draining,
            _state.State?.UpdateInterruptFence?.PendingId,
            _info?.ConnectionGeneration);
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

    public async Task UpdateRuntimeIdentityAsync(
        string? buildGitHash,
        string? component,
        string? version,
        string? sourceRevision,
        string? treeHash,
        string? artifactDigest,
        string? releaseId,
        long? generation,
        string? connectionGeneration = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_info is null)
            {
                _pendingRuntimeIdentity = new PendingRuntimeIdentity(
                    NormalizeIdentity(buildGitHash),
                    NormalizeIdentity(component),
                    NormalizeIdentity(version),
                    NormalizeIdentity(sourceRevision),
                    NormalizeIdentity(treeHash),
                    NormalizeIdentity(artifactDigest),
                    NormalizeIdentity(releaseId),
                    generation is > 0 ? generation : null,
                    NormalizeIdentity(connectionGeneration));
                _pendingBuildGitHash = _pendingRuntimeIdentity.BuildGitHash;
                return;
            }

            var normalizedConnectionGeneration = NormalizeIdentity(connectionGeneration);
            if (IsStaleConnectionGeneration(_info.ConnectionGeneration, normalizedConnectionGeneration))
                return;

            var next = _info with
            {
                BuildGitHash = NormalizeIdentity(buildGitHash) ?? _info.BuildGitHash,
                Component = NormalizeIdentity(component) ?? _info.Component,
                Version = NormalizeIdentity(version) ?? _info.Version,
                SourceRevision = NormalizeIdentity(sourceRevision) ?? _info.SourceRevision,
                TreeHash = NormalizeIdentity(treeHash) ?? _info.TreeHash,
                ArtifactDigest = NormalizeIdentity(artifactDigest) ?? _info.ArtifactDigest,
                ReleaseId = NormalizeIdentity(releaseId) ?? _info.ReleaseId,
                Generation = generation is > 0 ? generation : _info.Generation,
                ConnectionGeneration = normalizedConnectionGeneration ?? _info.ConnectionGeneration,
            };
            if (Equals(next, _info))
                return;

            SetRunnerInfo(next);
            await PersistAsync();
            if (_status == RunnerStatus.Online)
                await UpsertRegistryAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsStaleConnectionGeneration(string? current, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(current))
            return false;
        if (string.IsNullOrWhiteSpace(incoming))
            return true;
        var currentParts = current.Split(':', 2, StringSplitOptions.None);
        var incomingParts = incoming.Split(':', 2, StringSplitOptions.None);
        if (currentParts.Length == 2
            && incomingParts.Length == 2
            && long.TryParse(currentParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var currentValue)
            && long.TryParse(incomingParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var incomingValue))
        {
            return string.Equals(currentParts[0], incomingParts[0], StringComparison.Ordinal)
                && incomingValue < currentValue;
        }
        return !string.Equals(current, incoming, StringComparison.Ordinal);
    }

    private RunnerInfo InfoForRegister(RunnerInfo info)
    {
        return info with
        {
            BuildGitHash = info.BuildGitHash ?? _pendingRuntimeIdentity?.BuildGitHash ?? _pendingBuildGitHash,
            Component = info.Component ?? _pendingRuntimeIdentity?.Component,
            Version = info.Version ?? _pendingRuntimeIdentity?.Version,
            SourceRevision = info.SourceRevision ?? _pendingRuntimeIdentity?.SourceRevision,
            TreeHash = info.TreeHash ?? _pendingRuntimeIdentity?.TreeHash,
            ArtifactDigest = info.ArtifactDigest ?? _pendingRuntimeIdentity?.ArtifactDigest,
            ReleaseId = info.ReleaseId ?? _pendingRuntimeIdentity?.ReleaseId,
            Generation = info.Generation ?? _pendingRuntimeIdentity?.Generation,
            ConnectionGeneration = info.ConnectionGeneration ?? _pendingRuntimeIdentity?.ConnectionGeneration,
            RegisteredAt = info.RegisteredAt ?? _timeProvider.GetUtcNow(),
            ActionCatalog = info.ActionCatalog,
        };
    }

    private RunnerInfo InfoForHeartbeat(RunnerInfo info)
    {
        return info with
        {
            BuildGitHash = info.BuildGitHash ?? _pendingBuildGitHash ?? _info?.BuildGitHash,
            Component = info.Component ?? _info?.Component,
            Version = info.Version ?? _info?.Version,
            SourceRevision = info.SourceRevision ?? _info?.SourceRevision,
            TreeHash = info.TreeHash ?? _info?.TreeHash,
            ArtifactDigest = info.ArtifactDigest ?? _info?.ArtifactDigest,
            ReleaseId = info.ReleaseId ?? _info?.ReleaseId,
            Generation = info.Generation ?? _info?.Generation,
            ConnectionGeneration = info.ConnectionGeneration ?? _info?.ConnectionGeneration,
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

    private sealed record PendingRuntimeIdentity(
        string? BuildGitHash,
        string? Component,
        string? Version,
        string? SourceRevision,
        string? TreeHash,
        string? ArtifactDigest,
        string? ReleaseId,
        long? Generation,
        string? ConnectionGeneration);

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

            _pollAdmissionToken = null;
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
        var workerId = RunnerId;

        foreach (var workflowRunId in await _workflowRuns.FindRunningAssignedToAsync(workerId))
        {
            try
            {
                var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
                var observation = await workflow.ObserveAgentRunnerDisconnectedAsync(workerId);
                if (observation == ReportAck.Stale)
                    await workflow.InterruptActiveWorkAsync(workerId, "runner-lost");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "runner {runner} failed to close active workflow work for run {run}",
                    RunnerId, workflowRunId);
            }
        }

        var recoveryDeadlineAt = _timeProvider.GetUtcNow()
            + _agentJobOptions.RunnerLossRecoveryTimeout;
        foreach (var record in await _agentJobStore.ListRunningForRunnerAsync(workerId))
        {
            try
            {
                await GrainFactory.GetGrain<IAgentJobGrain>(record.JobKey)
                    .MarkUnknownAsync(AgentJobFailureReasons.RunnerLost, recoveryDeadlineAt);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "runner {runner} failed to enter AgentJob {job} recovery projection",
                    RunnerId,
                    record.JobKey);
            }
        }
    }

    private static void ValidateRunnerLossRecoveryTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.FromMinutes(2))
            throw new InvalidOperationException(
                "AgentJob RunnerLossRecoveryTimeout must be longer than the two-minute runner presence timeout.");
    }

    private void SetRunnerInfo(RunnerInfo? info)
    {
        var retained = info is null
            ? null
            : info with
            {
                ActionCatalog = CloneActionCatalog(info.ActionCatalog),
                RuntimeCatalogs = CloneRuntimeCatalogs(info.RuntimeCatalogs),
            };
        _info = retained;
        var state = _state.State ??= new RunnerState();
        state.LastKnownInfo = retained;
        state.LastKnownActionCatalogJson = retained?.ActionCatalog is { } catalog
            ? JSON.Serialize(catalog)
            : null;
    }

    private RunnerUpdateInterruptFence UpdateInterruptFence()
    {
        var state = _state.State ??= new RunnerState();
        return state.UpdateInterruptFence ??= new RunnerUpdateInterruptFence();
    }

    private async Task PersistUpdateInterruptFenceAsync(
        RunnerUpdateInterruptFence fence,
        string? pendingId,
        string? lastCancelledId)
    {
        var previousPendingId = fence.PendingId;
        var previousLastCancelledId = fence.LastCancelledId;
        fence.PendingId = pendingId;
        fence.LastCancelledId = lastCancelledId;
        try
        {
            await PersistAsync();
        }
        catch
        {
            fence.PendingId = previousPendingId;
            fence.LastCancelledId = previousLastCancelledId;
            throw;
        }
    }

    private static string? NormalizeUpdateInterruptId(string? updateInterruptId)
    {
        if (string.IsNullOrWhiteSpace(updateInterruptId))
            return null;
        return Guid.TryParse(updateInterruptId, out var parsed)
            ? parsed.ToString("N")
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
                action.Description,
                action.Capabilities is null ? null : [.. action.Capabilities])).ToArray(),
            catalog.Tombstones.Select(tombstone => new ActionCatalogTombstone(tombstone.Name, tombstone.Guidance)).ToArray());
    }

    private static Dictionary<string, RuntimeCatalogEntry>? CloneRuntimeCatalogs(
        Dictionary<string, RuntimeCatalogEntry>? catalogs)
    {
        if (catalogs is null)
            return null;

        return catalogs.ToDictionary(
            entry => entry.Key,
            entry => new RuntimeCatalogEntry(
                entry.Value.Models is null ? null : [.. entry.Value.Models],
                CloneMap(entry.Value.Variants),
                entry.Value.SupportsReasoningEffort,
                entry.Value.Complete, entry.Value.CapabilityRevision,
                CloneMap(entry.Value.ReasoningEfforts)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string[]>? CloneMap(Dictionary<string, string[]>? values) =>
        values?.ToDictionary(
            entry => entry.Key,
            entry => entry.Value is null ? Array.Empty<string>() : [.. entry.Value],
            StringComparer.OrdinalIgnoreCase);

    private static System.Text.Json.JsonElement? CloneDefault(System.Text.Json.JsonElement? value)
    {
        if (value is not { } element || element.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            return null;
        return element.Clone();
    }

}
