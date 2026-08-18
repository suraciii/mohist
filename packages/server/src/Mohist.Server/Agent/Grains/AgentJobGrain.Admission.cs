using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workspace.Grains;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Admission, runner election, and concurrency-permit lifecycle for
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
        // poll path. Re-admitting here would clobber ReadySince and
        // extend the deadline; only re-admit when no runner was found.
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
                State.ReadySince = null;
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
            State.ReadySince = null;
            await PersistAsync();
            }
        }

        if (!await AcquireConcurrencyPermitAsync())
            return;

        State.WaitingReason = null;
        await PersistAsync();

        var projectId = State.Input.ProjectId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pinnedRunnerId))
        {
            State.WaitingReason = null;
            if (!await TryAdmitOnRunnerAsync(pinnedRunnerId))
            {
                State.WaitingReason = AgentAvailabilityWaitReasons.NoOnlineRunner;
                await ReleaseConcurrencyPermitAsync();
                await PersistAsync();
            }
            return;
        }

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListEligibleRunnersAsync(projectId);
        if (runners.Count == 0)
        {
            State.WaitingReason = AgentAvailabilityWaitReasons.NoOnlineRunner;
            await ReleaseConcurrencyPermitAsync();
            return;
        }

        // Workspace affinity: a bound job routes to the workspace's home
        // runner first. A stale home (runner offline) is cleared and the
        // job falls back to the generic election; the runner that wins
        // materializes the workspace and reports the new home.
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

        State.WaitingReason = AgentAvailabilityWaitReasons.CapacityFull;
        await ReleaseConcurrencyPermitAsync();
        await PersistAsync();
    }

    private async Task<bool> AcquireConcurrencyPermitAsync()
    {
        if (State.Input is null)
            return false;

        var projectId = State.Input.ProjectId;
        var agentId = State.Input.AgentId;
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(agentId))
            return true;

        var token = State.ConcurrencyPermitToken ??= $"{Key}:execution";
        var dispatchId = State.ConcurrencyDispatchId ??= $"job:{Key}";
        var gate = _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));
        var result = await gate.AcquireAsync(
            projectId,
            agentId,
            token,
            Key,
            AgentConcurrencyPermitOwnerKind.Job,
            dispatchId);
        if (result == AgentConcurrencyAcquireResult.Waiting)
        {
            var waiter = (await gate.GetSnapshotAsync()).Waiters.FirstOrDefault(candidate =>
                string.Equals(candidate.Token, token, StringComparison.Ordinal)
                && string.Equals(candidate.OwnerId, Key, StringComparison.Ordinal));
            State.ConcurrencyPermitHeld = false;
            State.ConcurrencyPermitId = null;
            State.ConcurrencyWaiterId = waiter?.WaiterId;
            State.ConcurrencyGeneration = waiter?.Generation ?? State.ConcurrencyGeneration;
            State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.DispatchPending;
            State.WaitingReason = AgentAvailabilityWaitReasons.CapacityFull;
            await PersistAsync();
            return false;
        }

        var permit = await gate.GetPermitAsync(token);
        State.ConcurrencyPermitHeld = permit is not null;
        State.ConcurrencyPermitId = permit?.PermitId;
        State.ConcurrencyWaiterId = null;
        State.ConcurrencyGeneration = permit?.Generation ?? 0;
        State.ConcurrencyDispatchId = permit?.DispatchId ?? dispatchId;
        State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.DispatchPending;
        State.WaitingReason = AgentAvailabilityWaitReasons.DispatchPending;
        await PersistAsync();
        if (permit is not null)
            await gate.ConfirmDispatchPendingAsync(projectId, agentId, token, permit.PermitId!, permit.DispatchId!);
        return true;
    }

    public async Task ConcurrencyPermitGrantedAsync(
        string? token = null,
        string? permitId = null,
        string? dispatchId = null)
    {
        await HydrateAsync();
        if (token is not null
            && !string.Equals(State.ConcurrencyPermitToken, token, StringComparison.Ordinal))
            return;
        if (permitId is not null
            && State.ConcurrencyPermitId is not null
            && !string.Equals(State.ConcurrencyPermitId, permitId, StringComparison.Ordinal))
            return;
        if (dispatchId is not null
            && State.ConcurrencyDispatchId is not null
            && !string.Equals(State.ConcurrencyDispatchId, dispatchId, StringComparison.Ordinal))
            return;
        if (State.Status == AgentJobStatus.Pending)
            await TryAdmitAsync();
    }

    private async Task ReleaseConcurrencyPermitAsync()
    {
        if (State.Input is null)
            return;

        var projectId = State.Input.ProjectId;
        var agentId = State.Input.AgentId;
        var token = State.ConcurrencyPermitToken;
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(token))
        {
            State.ConcurrencyPermitHeld = false;
            return;
        }

        State.ConcurrencyPermitHeld = false;
        State.ConcurrencyReleasePending = true;
        await PersistAsync();
        await TryReleaseConcurrencyPermitAsync();
    }

    private async Task TryReleaseConcurrencyPermitAsync()
    {
        if (!State.ConcurrencyReleasePending
            || State.Input is null
            || string.IsNullOrWhiteSpace(State.Input.ProjectId)
            || string.IsNullOrWhiteSpace(State.Input.AgentId)
            || string.IsNullOrWhiteSpace(State.ConcurrencyPermitToken))
            return;

        try
        {
            var projectId = State.Input.ProjectId;
            var agentId = State.Input.AgentId;
            var gate = _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));
            if (State.ConcurrencyPermitId is not null
                && State.ConcurrencyDispatchId is not null)
            {
                await gate.MarkTerminalAsync(
                    projectId,
                    agentId,
                    State.ConcurrencyPermitToken,
                    State.ConcurrencyPermitId,
                    State.ConcurrencyDispatchId,
                    State.Status == AgentJobStatus.Cancelled);
            }
            await gate.ReleaseAsync(
                projectId,
                agentId,
                State.ConcurrencyPermitToken,
                State.ConcurrencyPermitId,
                State.ConcurrencyGeneration == 0 ? null : State.ConcurrencyGeneration,
                State.ConcurrencyWaiterId);
            State.ConcurrencyReleasePending = false;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} could not release concurrency permit {Token}; recovery reminder will retry",
                Key,
                State.ConcurrencyPermitToken);
        }
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
        // the job to Running; the next poll claim does that.
        var now = _timeProvider.GetUtcNow();
        var workId = State.RecoveryGeneration > 0 && !string.IsNullOrWhiteSpace(State.InterruptedWorkId)
            ? RecoveryWorkId(State.InterruptedWorkId!, State.RecoveryGeneration)
            : StableWorkId(Key);
        var dispatch = await BuildDispatchAsync(workId);

        State.RunnerId = runnerId;
        State.WorkId = workId;
        State.ReadySince = now;
        State.RunnerAccepted = false;
        State.RunningSince = null;
        UpdateRecoveryAttempt(workId, runnerId, AgentJobStatus.Pending);

        var record = new AgentJobLedgerRecord(
            JobKey: Key,
            StateJson: JsonSerializer.Serialize(State, JSON.Options),
            Revision: _ledger?.Revision ?? 0,
            AssignedRunnerId: runnerId,
            WorkId: workId,
            ReadySince: now,
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
            Key, runnerId, workId, now);

        await EnsureRecoveryReminderAsync();

        if (State.ConcurrencyPermitHeld
            && State.ConcurrencyPermitId is not null
            && State.ConcurrencyDispatchId is not null
            && State.Input?.ProjectId is { } projectId
            && State.Input.AgentId is { } agentId)
        {
            await _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
                .MarkDispatchedAsync(
                    projectId,
                    agentId,
                    State.ConcurrencyPermitToken!,
                    State.ConcurrencyPermitId,
                    State.ConcurrencyDispatchId);
            State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.Dispatched;
            await PersistAsync();
        }

        // The test-only signal is the admission boundary: all durable
        // assignment and concurrency state must be visible before polling.
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
