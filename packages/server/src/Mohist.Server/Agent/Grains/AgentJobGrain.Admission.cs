using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workspace.Grains;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Admission, runner election, and derived Agent-capacity claiming for
/// an <see cref="AgentJobGrain"/>. Split from the grain file under the
/// line-count ratchet; all methods share the partial class's private
/// state via the host.
/// </summary>
public sealed partial class AgentJobGrain
{
    private async Task TryAdmitAsync()
    {
        if (State.LaunchVisibility != AgentLaunchVisibility.Visible)
            return;
        if (State.Status != AgentJobStatus.Pending || State.Input is null || State.SubmittedAt is null)
            return;

        // If the row already carries a dispatch snapshot (a previous
        // admission succeeded), the next claim race is owned by the
        // poll path. Re-admitting here would extend the deadline; the
        // first capacity claim's timestamps stay fixed, so only the
        // assignment is revised.
        var pinnedRunnerId = State.Input.PinnedRunnerId;
        if (!string.IsNullOrWhiteSpace(State.RunnerId)
            && !string.IsNullOrWhiteSpace(_ledger?.DispatchJson)
            && !string.IsNullOrWhiteSpace(State.WorkId))
        {
            if (!string.IsNullOrWhiteSpace(pinnedRunnerId)
                && !string.Equals(State.RunnerId, pinnedRunnerId, StringComparison.Ordinal))
            {
                State.RunnerId = null;
                State.WorkId = null;
                State.RunnerAccepted = false;
                State.RunningSince = null;
                await PersistAsync();
            }
            else
            {
                var assignedRunner = GrainFactory.GetGrain<IRunnerGrain>(State.RunnerId);
                if ((await assignedRunner.GetRuntimeStateAsync()).Status == RunnerStatus.Online)
                    return;

                State.RunnerId = null;
                State.RunnerAccepted = false;
                State.RunningSince = null;
                await PersistAsync();
            }
        }

        if (!await ClaimAgentCapacityAsync())
            return;

        var projectId = State.Input.ProjectId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pinnedRunnerId))
        {
            if (await TryAdmitOnRunnerAsync(pinnedRunnerId))
                return;
            await SetWaitingReasonAsync(AgentAvailabilityWaitReasons.NoOnlineRunner);
            return;
        }

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListEligibleRunnersAsync(projectId);
        if (runners.Count == 0)
        {
            await SetWaitingReasonAsync(AgentAvailabilityWaitReasons.NoOnlineRunner);
            return;
        }

        // Workspace affinity: a bound job routes to the workspace's home
        // runner first. A stale home (runner offline) is cleared and the
        // job falls back to the generic election; the runner that wins
        // provisions the workspace and reports the new home.
        if (!string.IsNullOrWhiteSpace(State.Input.WorkspaceName)
            && !string.IsNullOrWhiteSpace(State.Input.ProjectId))
        {
            var workspace = GrainFactory.GetGrain<IWorkspaceGrain>(
                GrainKey.Workspace(State.Input.ProjectId, State.Input.WorkspaceName));
            var home = await workspace.GetHomeAsync();
            if (home is not null)
            {
                var homeRunner = GrainFactory.GetGrain<IRunnerGrain>(home.RunnerId);
                var homeState = await homeRunner.GetRuntimeStateAsync();
                if (homeState.Status == RunnerStatus.Online
                    && await TryAdmitOnRunnerAsync(home.RunnerId))
                {
                    return;
                }

                if (homeState.Status != RunnerStatus.Online)
                    await workspace.ClearHomeIfAsync(home.RunnerId);
            }
        }

        foreach (var runnerInfo in runners)
        {
            if (await TryAdmitOnRunnerAsync(runnerInfo.RunnerId))
                return;
        }

        await SetWaitingReasonAsync(AgentAvailabilityWaitReasons.CapacityFull);
    }

    /// <summary>
    /// Claims Agent occupancy in the derived capacity store before Runner
    /// election. The claim runs in the store's single SQLite immediate
    /// transaction against the actually persisted ledger revision, so it
    /// happens even when no Runner is online and a concurrent writer
    /// cannot split counting from claiming. The durable recovery
    /// reminder is armed first: a crash after the commit still leaves a
    /// wake-up that re-evaluates from the committed row.
    /// </summary>
    private async Task<bool> ClaimAgentCapacityAsync()
    {
        if (State.CapacityClaimedAt is not null)
            return true;

        // The accepted input must be on the row before the claim
        // transaction can read it.
        if (_ledger is null)
            await PersistAsync();
        await EnsureRecoveryReminderAsync();

        AgentJobCapacityClaimResult result;
        try
        {
            result = await _capacityStore.ClaimJobAsync(Key, _ledger!.Revision);
        }
        catch (Exception ex)
        {
            // An exception leaves the commit outcome unknown: the row may
            // already hold the claim. Reload the authoritative row before
            // this activation takes any further effect and let the
            // reminder retry from those facts; treating the claim as
            // failed here could admit a second occupant.
            _log.LogWarning(ex,
                "AgentJob {Id} capacity claim outcome is uncertain; state reloaded before any further effect",
                Key);
            _hydrated = false;
            await HydrateAsync();
            return false;
        }

        switch (result.Disposition)
        {
            case AgentCapacityClaimDisposition.Claimed:
            case AgentCapacityClaimDisposition.AlreadyClaimed:
                if (result.Job is not null)
                    InstallCommittedLedger(result.Job);
                await SetWaitingReasonAsync(AgentAvailabilityWaitReasons.DispatchPending);
                return true;

            case AgentCapacityClaimDisposition.Conflict:
                // A concurrent writer moved the row; only a fresh reload
                // makes a retry safe. Never save the stale cache over it.
                _hydrated = false;
                await HydrateAsync();
                return false;

            case AgentCapacityClaimDisposition.Incomplete:
                // Missing Agent identity or definition, or owner evidence
                // that cannot attribute this Job: the accepted work stays
                // Pending until the definition or identity is repaired.
                await SetWaitingReasonAsync(AgentAvailabilityWaitReasons.DispatchPending);
                return false;

            case AgentCapacityClaimDisposition.CapacityFull:
            case AgentCapacityClaimDisposition.NotInOrder:
            case AgentCapacityClaimDisposition.NotEligible:
                // Accepted work waiting behind other occupants or behind
                // its own Session's earlier deliverable Turn. It never
                // fails from this fact; the reminder re-evaluates.
                await SetWaitingReasonAsync(AgentAvailabilityWaitReasons.CapacityFull);
                return false;

            default:
                return false;
        }
    }

    private async Task SetWaitingReasonAsync(string? reason)
    {
        if (State.WaitingReason == reason)
            return;
        State.WaitingReason = reason;
        await PersistAsync();
    }

    /// <summary>
    /// Installs a ledger record that a storage transaction just committed
    /// on this owner's behalf (the derived capacity claim). The committed
    /// row — including its incremented revision and the claim facts — is the
    /// only valid base for the next save or dispatch; a stale cache over it
    /// would write a lost update.
    /// </summary>
    private void InstallCommittedLedger(AgentJobLedgerRecord record)
    {
        _ledger = record;
        _state = JsonSerializer.Deserialize<AgentJobState>(record.StateJson, JSON.Options) ?? new AgentJobState();
        BackfillSchedulingFieldsFromRecord(record);
        _hydrated = true;
    }

    private void BackfillSchedulingFieldsFromRecord(AgentJobLedgerRecord record)
    {
        if (_state is null)
            return;
        // Backfill scheduling fields from the row so callers that read
        // state see the indexed values too.
        _state.RunnerId ??= record.AssignedRunnerId;
        _state.WorkId ??= record.WorkId;
        _state.SubmittedAt ??= record.ReadySince;
        _state.ReadySince ??= record.ReadySince;
        _state.RunningSince ??= record.RunningSince;
        if (Enum.TryParse<AgentLaunchVisibility>(record.LaunchVisibility, true, out var visibility))
            _state.LaunchVisibility = visibility;
    }

    private async Task<bool> TryAdmitOnRunnerAsync(string runnerId)
    {
        var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        if (state.Status != RunnerStatus.Online)
            return false;

        var maxSlots = await runner.GetSlotsAsync();
        var activeWorkCount = state.ActiveWorks
            .Select(w => w.OwnerId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (activeWorkCount >= maxSlots)
            return false;

        // Admission writes the ledger row directly. The grain does not
        // call RunnerGrain.AssignAgentJobAsync and does not transition
        // the job to Running; the next poll claim does that. The first
        // capacity claim fixed ReadySince; assignment never restamps it.
        var workId = StableWorkId(Key);
        var dispatch = await BuildDispatchAsync(workId);

        State.RunnerId = runnerId;
        State.WorkId = workId;
        State.ReadySince = _ledger?.ReadySince ?? State.ReadySince;
        State.RunnerAccepted = false;
        State.RunningSince = null;

        var record = new AgentJobLedgerRecord(
            JobKey: Key,
            StateJson: JsonSerializer.Serialize(State, JSON.Options),
            Revision: _ledger?.Revision ?? 0,
            AssignedRunnerId: runnerId,
            WorkId: workId,
            ReadySince: State.ReadySince,
            RunningSince: null,
            DispatchJson: JsonSerializer.Serialize(dispatch, JSON.Options),
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
            IssueProjectId: State.Input?.ProjectId,
            IssueNumber: State.Input?.IssueNumber,
            AgentSessionId: State.Input?.AgentSessionId,
            InitialInputId: State.Input?.InitialInputId,
            InitialTurnId: State.Input?.InitialTurnId,
            PinnedRunnerId: State.Input?.PinnedRunnerId,
            LaunchVisibility: State.LaunchVisibility.ToString().ToLowerInvariant());

        if (_ledger is null)
        {
            var inserted = await _jobStore.InsertLedgerAsync(record);
            _ledger = inserted;
            await HydrateAsync();
        }
        else
        {
            var saved = await _jobStore.SaveLedgerAsync(record);
            _ledger = saved;
            await HydrateAsync();
        }

        _log.LogInformation(
            "AgentJob {Id} admitted to runner {Runner} as work {Work} (readySince={ReadySince})",
            Key, runnerId, workId, State.ReadySince);

        await EnsureRecoveryReminderAsync();

        // The test-only signal is the admission boundary: all durable
        // assignment and capacity state must be visible before polling.
        await SafeAssignmentPreparedAsync(runnerId, workId);

        return true;
    }

    private async Task SafeAssignmentPreparedAsync(string runnerId, string workId)
    {
        try
        {
            await _dispatchObserver.AssignmentPreparedAsync(Key, runnerId, workId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} dispatch observer AssignmentPrepared threw; ledger row remains authoritative",
                Key);
        }
    }

    private async Task SafeRunnerAcceptedAsync(string runnerId, string workId)
    {
        try
        {
            await _dispatchObserver.RunnerAcceptedAsync(Key, runnerId, workId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} dispatch observer RunnerAccepted threw; claim remains authoritative",
                Key);
        }
    }
}
