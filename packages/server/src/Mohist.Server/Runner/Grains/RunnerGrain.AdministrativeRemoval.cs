using Mohist.Server.Auth.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    private const string AdministrativeRemovalReminderName = "administrative-removal";
    private static readonly TimeSpan AdministrativeRemovalRetryInterval = TimeSpan.FromSeconds(10);

    public async Task<RunnerAdministrativeRemovalResult> RevokeExecutionAuthorityAsync(DateTimeOffset revokedAt)
    {
        RunnerAdministrativeRemoval? removal;
        await _lifecycleGate.WaitAsync();
        try
        {
            var state = _state.State ??= new RunnerState();
            removal = state.AdministrativeRemoval;
            if (removal is null)
            {
                var authority = await _credentialStatus.GetActiveAuthorityAsync(RunnerId);
                if (authority is null)
                    return new RunnerAdministrativeRemovalResult(false, revokedAt, false);

                removal = new RunnerAdministrativeRemoval
                {
                    RemovalId = Guid.NewGuid().ToString("N"),
                    RevokedAt = revokedAt,
                    Phase = RunnerAdministrativeRemovalPhase.IntentRecorded,
                    RemovedProcessGeneration = state.CurrentProcessGeneration,
                    RemovedCredentialId = authority.CredentialId,
                };
                var wasDraining = _draining;
                state.AdministrativeRemoval = removal;
                _draining = true;
                try
                {
                    await PersistAsync();
                }
                catch
                {
                    state.AdministrativeRemoval = null;
                    _draining = wasDraining;
                    throw;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await EnsureAdministrativeRemovalReminderAsync();
        await ContinueAdministrativeRemovalAsync();
        removal = (_state.State ??= new RunnerState()).AdministrativeRemoval;
        if (removal?.Phase != RunnerAdministrativeRemovalPhase.Completed)
            throw new InvalidOperationException(
                $"Runner {RunnerId} administrative removal is durably pending and will retry.");
        return new RunnerAdministrativeRemovalResult(true, removal.RevokedAt, Completed: true);
    }

    private async Task ContinueAdministrativeRemovalAsync()
    {
        var removal = (_state.State ??= new RunnerState()).AdministrativeRemoval;
        if (removal is null || removal.Phase == RunnerAdministrativeRemovalPhase.Completed)
            return;

        try
        {
            if (removal.Phase == RunnerAdministrativeRemovalPhase.IntentRecorded)
            {
                var revoked = await _credentials.RevokeRunnerCredentialAsync(RunnerId, removal.RevokedAt);
                if (!revoked
                    && await _credentialStatus.GetStatusAsync(RunnerId) != RunnerCredentialStatus.Revoked)
                {
                    throw new InvalidOperationException(
                        $"Runner {RunnerId} credential could not be fenced for administrative removal.");
                }
                await AdvanceAdministrativeRemovalAsync(
                    removal.RemovalId,
                    RunnerAdministrativeRemovalPhase.CredentialRevoked);
            }

            removal = CurrentAdministrativeRemoval(removal.RemovalId);
            if (removal.Phase == RunnerAdministrativeRemovalPhase.CredentialRevoked)
            {
                await PersistAdministrativeAuthorityFenceAsync(removal.RemovalId);
            }

            removal = CurrentAdministrativeRemoval(removal.RemovalId);
            if (removal.Phase == RunnerAdministrativeRemovalPhase.AuthorityFenced)
            {
                // The durable process-generation fence is already committed.
                // This closes only the current Server transport; it makes no
                // claim about an external Runtime process or side effect.
                await _authorityFence.FenceAsync(
                    RunnerId,
                    removal.RemovedProcessGeneration);
                await GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                    .UnregisterAsync(RunnerId);

                // Workflow owns its own runner-lost decision. With no process
                // generation authoritative, every generation-bound owner claim
                // is enumerated and settled. AgentJobs remain excluded; Session
                // convergence publishes their settlement through the event bus.
                if (!await ReconcileAllWorkflowClaimsForAdministrativeRemovalAsync())
                    throw new InvalidOperationException(
                        $"Runner {RunnerId} workflow closeout is still pending during administrative removal.");

                await AdvanceAfterAdministrativeWorkflowCloseoutAsync(removal.RemovalId);
            }

            removal = CurrentAdministrativeRemoval(removal.RemovalId);
            if (removal.Phase == RunnerAdministrativeRemovalPhase.SessionsSettling)
            {
                var sessionIds = await _sessions.ListSessionIdsByRunnerAsync(RunnerId);
                foreach (var sessionId in sessionIds)
                {
                    var session = GrainFactory.GetGrain<IAgentSessionGrain>(sessionId);
                    var probe = await session.PrepareActivityProbeAsync(RunnerId, runnerRemoved: true);
                    if (probe is null)
                        continue;
                    if (!probe.HasCompleteTarget()
                        || !string.Equals(probe.RunnerId, RunnerId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"AgentSession {sessionId} returned an incomplete administrative activity observation.");
                    }

                    var applied = await session.ApplyActivityProbeAsync(new RunnerSessionActivityProbeResult(
                        probe,
                        RunnerSessionActivityObservations.UnknownToRunner));
                    if (!applied)
                        throw new InvalidOperationException(
                            $"AgentSession {sessionId} administrative activity observation became stale.");
                }

                await AdvanceAdministrativeRemovalAsync(
                    removal.RemovalId,
                    RunnerAdministrativeRemovalPhase.Completed);
                await RemoveAdministrativeRemovalReminderAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Runner {RunnerId} administrative removal {RemovalId} will retry",
                RunnerId,
                removal.RemovalId);
            await EnsureAdministrativeRemovalReminderAsync();
        }
    }

    private async Task PersistAdministrativeAuthorityFenceAsync(string removalId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var removal = CurrentAdministrativeRemoval(removalId);
            if (removal.Phase != RunnerAdministrativeRemovalPhase.CredentialRevoked)
                return;

            var state = _state.State ??= new RunnerState();
            var previousRemovedGeneration = removal.RemovedProcessGeneration;
            var previousCurrent = state.CurrentProcessGeneration;
            var previousPending = state.PendingProcessGeneration;
            var previousClosing = state.ClosingProcessGeneration;
            var previousLease = state.PresenceLeaseExpiresAt;
            var previousInfo = _info;
            var previousStoredInfo = state.LastKnownInfo;
            var previousCatalog = state.LastKnownActionCatalogJson;
            var previousPoll = _pollAdmissionToken;
            var previousObservation = _dispatchObservation;
            var previousStatus = _status;
            var previousDraining = _draining;
            var previousPhase = removal.Phase;
            removal.RemovedProcessGeneration ??= state.CurrentProcessGeneration;
            state.CurrentProcessGeneration = null;
            state.PendingProcessGeneration = null;
            if (string.IsNullOrWhiteSpace(state.ClosingProcessGeneration))
                state.ClosingProcessGeneration = removal.RemovedProcessGeneration;
            state.PresenceLeaseExpiresAt = null;
            _pollAdmissionToken = null;
            _dispatchObservation = null;
            _status = RunnerStatus.Offline;
            SetRunnerInfo(null);
            _draining = true;
            removal.Phase = RunnerAdministrativeRemovalPhase.AuthorityFenced;
            try
            {
                await PersistAsync();
            }
            catch
            {
                removal.RemovedProcessGeneration = previousRemovedGeneration;
                removal.Phase = previousPhase;
                state.CurrentProcessGeneration = previousCurrent;
                state.PendingProcessGeneration = previousPending;
                state.ClosingProcessGeneration = previousClosing;
                state.PresenceLeaseExpiresAt = previousLease;
                state.LastKnownInfo = previousStoredInfo;
                state.LastKnownActionCatalogJson = previousCatalog;
                _info = previousInfo;
                _pollAdmissionToken = previousPoll;
                _dispatchObservation = previousObservation;
                _status = previousStatus;
                _draining = previousDraining;
                throw;
            }
            PublishStatusObservation();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task AdvanceAfterAdministrativeWorkflowCloseoutAsync(string removalId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var removal = CurrentAdministrativeRemoval(removalId);
            if (removal.Phase != RunnerAdministrativeRemovalPhase.AuthorityFenced)
                return;
            var state = _state.State ??= new RunnerState();
            var previousClosing = state.ClosingProcessGeneration;
            var previousPhase = removal.Phase;
            state.ClosingProcessGeneration = null;
            removal.Phase = RunnerAdministrativeRemovalPhase.SessionsSettling;
            try
            {
                await PersistAsync();
            }
            catch
            {
                state.ClosingProcessGeneration = previousClosing;
                removal.Phase = previousPhase;
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task AdvanceAdministrativeRemovalAsync(
        string removalId,
        RunnerAdministrativeRemovalPhase phase)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var removal = CurrentAdministrativeRemoval(removalId);
            if (removal.Phase >= phase)
                return;
            var previous = removal.Phase;
            removal.Phase = phase;
            try
            {
                await PersistAsync();
            }
            catch
            {
                removal.Phase = previous;
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private RunnerAdministrativeRemoval CurrentAdministrativeRemoval(string removalId)
    {
        var current = _state.State?.AdministrativeRemoval;
        if (current is null || !string.Equals(current.RemovalId, removalId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Runner {RunnerId} administrative removal was superseded.");
        return current;
    }

    private async Task ReconcileAdministrativeRemovalForRegistrationAsync()
    {
        var removal = _state.State?.AdministrativeRemoval;
        if (removal is null)
            return;
        if (removal.Phase != RunnerAdministrativeRemovalPhase.Completed)
            throw new InvalidOperationException(
                $"Runner {RunnerId} administrative removal is still pending.");

        var authority = await _credentialStatus.GetActiveAuthorityAsync(RunnerId);
        if (authority is null
            || string.IsNullOrWhiteSpace(removal.RemovedCredentialId)
            || string.Equals(
                authority.CredentialId,
                removal.RemovedCredentialId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Runner {RunnerId} execution authority remains revoked.");

        await _lifecycleGate.WaitAsync();
        try
        {
            var current = CurrentAdministrativeRemoval(removal.RemovalId);
            if (current.Phase != RunnerAdministrativeRemovalPhase.Completed)
                throw new InvalidOperationException(
                    $"Runner {RunnerId} administrative removal is still pending.");
            var state = _state.State ??= new RunnerState();
            var previousDraining = _draining;
            state.AdministrativeRemoval = null;
            _draining = !string.IsNullOrWhiteSpace(state.UpdateInterruptFence?.PendingId)
                || !string.IsNullOrWhiteSpace(state.PendingProcessGeneration)
                || !string.IsNullOrWhiteSpace(state.ClosingProcessGeneration);
            try
            {
                await PersistAsync();
            }
            catch
            {
                state.AdministrativeRemoval = current;
                _draining = previousDraining;
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        await RemoveAdministrativeRemovalReminderAsync();
    }

    private Task EnsureAdministrativeRemovalReminderAsync() =>
        this.RegisterOrUpdateReminder(
            AdministrativeRemovalReminderName,
            AdministrativeRemovalRetryInterval,
            AdministrativeRemovalRetryInterval);

    private async Task RemoveAdministrativeRemovalReminderAsync()
    {
        var reminder = await this.GetReminder(AdministrativeRemovalReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }
}
