using Mohist.Server.Auth.Domain;
using Mohist.Server.Runner.Services;
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
        RunnerAuthorityFenceResult? rollbackFence = null;
        Exception? intentFailure = null;
        var transportFenced = false;
        var intentRecorded = false;
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

                // The wake is durable before pending intent can exist. A tick
                // that observes no intent is an orphan and removes itself while
                // serialized with a possible new removal below.
                await EnsureAdministrativeRemovalReminderAsync();

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
                RunnerAuthorityFenceResult? fence = null;
                try
                {
                    // The local enqueue barrier still wins before intent is
                    // persisted. The registry returns the sessions whose normal
                    // finalizer callback it suppressed, so a certain rollback
                    // can deliver ordinary disconnect after releasing this gate.
                    fence = await _authorityFence.FenceAsync(
                        RunnerId,
                        removal.RemovedProcessGeneration);
                    transportFenced = true;
                    await _administrativeRemovalObserver.BeforeIntentWrite(RunnerId);
                    await PersistAsync();
                    await _administrativeRemovalObserver.AfterIntentWrite(RunnerId);
                    intentRecorded = true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        // A failed write reply is not proof that the write did
                        // not commit. Reload before either restoring authority or
                        // treating the durable intent as pending.
                        await _state.ReadStateAsync();
                    }
                    catch (Exception reloadException)
                    {
                        (_state.State ??= new RunnerState()).AdministrativeRemoval ??= removal;
                        _draining = true;
                        intentFailure = new AggregateException(ex, reloadException);
                    }

                    if (intentFailure is null)
                    {
                        var durableRemoval = _state.State?.AdministrativeRemoval;
                        if (durableRemoval is null)
                        {
                            _draining = wasDraining;
                            rollbackFence = fence;
                            transportFenced = false;
                            intentFailure = ex;
                        }
                        else if (string.Equals(
                            durableRemoval.RemovalId,
                            removal.RemovalId,
                            StringComparison.Ordinal))
                        {
                            removal = durableRemoval;
                            _draining = true;
                            intentRecorded = true;
                            _log.LogWarning(
                                ex,
                                "Runner {RunnerId} removal intent write reply failed but durable removal {RemovalId} was reloaded",
                                RunnerId,
                                durableRemoval.RemovalId);
                        }
                        else
                        {
                            _draining = true;
                            intentFailure = new AggregateException(
                                ex,
                                new InvalidOperationException(
                                    $"Runner {RunnerId} removal {removal.RemovalId} was superseded by durable removal {durableRemoval.RemovalId}."));
                        }
                    }
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (rollbackFence is not null)
        {
            try
            {
                await NotifyOrdinaryDisconnectAsync(rollbackFence.DisconnectedSessionIds);
            }
            catch (Exception disconnectException)
            {
                throw new AggregateException(intentFailure!, disconnectException);
            }
        }
        if (intentFailure is not null)
            throw intentFailure;

        if (intentRecorded)
            await _administrativeRemovalObserver.IntentRecorded(RunnerId);

        await ContinueAdministrativeRemovalAsync(removal!.RemovalId, transportFenced);
        removal = (_state.State ??= new RunnerState()).AdministrativeRemoval;
        if (removal?.Phase != RunnerAdministrativeRemovalPhase.Completed)
            throw new InvalidOperationException(
                $"Runner {RunnerId} administrative removal is durably pending and will retry.");
        return new RunnerAdministrativeRemovalResult(true, removal.RevokedAt, Completed: true);
    }

    private async Task ContinueAdministrativeRemovalAsync(
        string removalId,
        bool transportAlreadyFenced = false)
    {
        var removal = (_state.State ??= new RunnerState()).AdministrativeRemoval;
        if (removal is null
            || !string.Equals(removal.RemovalId, removalId, StringComparison.Ordinal)
            || removal.Phase == RunnerAdministrativeRemovalPhase.Completed)
            return;

        try
        {
            // Durable removal intent is already a closed transport authority.
            // This singleton call does not call back into the Runner grain, so
            // it cannot create a Runner -> transport -> Runner wait cycle.
            if (!transportAlreadyFenced)
            {
                await _authorityFence.FenceAsync(
                    RunnerId,
                    removal.RemovedProcessGeneration);
            }

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
                // The Server transport was already closed from durable intent;
                // this phase makes no claim about an external Runtime process
                // or side effect.
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
                await RemoveAdministrativeRemovalReminderIfSafeAsync(removal.RemovalId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Runner {RunnerId} administrative removal {RemovalId} will retry",
                RunnerId,
                removal.RemovalId);
            await EnsureAdministrativeRemovalReminderIfPendingAsync(removal.RemovalId);
        }
    }

    private async Task NotifyOrdinaryDisconnectAsync(IReadOnlyList<string> sessionIds)
    {
        await Task.WhenAll(sessionIds.Select(sessionId =>
            GrainFactory.GetGrain<IAgentSessionGrain>(sessionId).RunnerDisconnectedAsync()));
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
            var previousRegistrationCredential = state.CurrentRegistrationCredentialId;
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
            state.CurrentRegistrationCredentialId = null;
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
                state.CurrentRegistrationCredentialId = previousRegistrationCredential;
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

    private async Task ReconcileAdministrativeRemovalForRegistrationAsync(
        RunnerPresentedAuthority presentedAuthority)
    {
        await RequireActivePresentedCredentialAsync(presentedAuthority);
        var removal = _state.State?.AdministrativeRemoval;
        if (removal is null)
            return;
        if (removal.Phase != RunnerAdministrativeRemovalPhase.Completed)
            throw new InvalidOperationException(
                $"Runner {RunnerId} administrative removal is still pending.");
        if (presentedAuthority.OperatorOverride
            || string.IsNullOrWhiteSpace(removal.RemovedCredentialId)
            || string.Equals(
                presentedAuthority.CredentialId,
                removal.RemovedCredentialId,
                StringComparison.Ordinal))
            throw new RunnerCredentialAuthorityException(
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
            await RemoveAdministrativeRemovalReminderCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RequireActivePresentedCredentialAsync(
        RunnerPresentedAuthority presentedAuthority)
    {
        if (presentedAuthority.OperatorOverride)
        {
            if (!string.IsNullOrWhiteSpace(presentedAuthority.CredentialId))
                throw new RunnerCredentialAuthorityException(
                    "Operator Runner authority must not borrow an issued credential identity.");
            return;
        }

        if (string.IsNullOrWhiteSpace(presentedAuthority.CredentialId))
            throw new RunnerCredentialAuthorityException(
                $"Runner {RunnerId} registration is missing its presented credential identity.");
        var active = await _credentialStatus.GetActiveAuthorityAsync(RunnerId);
        if (active is null
            || !string.Equals(
                active.CredentialId,
                presentedAuthority.CredentialId,
                StringComparison.Ordinal))
            throw new RunnerCredentialAuthorityException(
                $"Runner {RunnerId} presented credential authority is not active.");
    }

    public async Task<bool> IsCurrentRegistrationAuthorityAsync(
        string processGeneration,
        RunnerPresentedAuthority presentedAuthority)
    {
        ArgumentNullException.ThrowIfNull(presentedAuthority);
        var state = _state.State;
        if (state is null
            || state.AdministrativeRemoval is not null
            || string.IsNullOrWhiteSpace(processGeneration)
            || !string.Equals(
                state.CurrentProcessGeneration,
                processGeneration,
                StringComparison.Ordinal))
            return false;
        if (presentedAuthority.OperatorOverride)
            return string.IsNullOrWhiteSpace(presentedAuthority.CredentialId);
        if (string.IsNullOrWhiteSpace(presentedAuthority.CredentialId)
            || !string.Equals(
                state.CurrentRegistrationCredentialId,
                presentedAuthority.CredentialId,
                StringComparison.Ordinal))
            return false;
        var active = await _credentialStatus.GetActiveAuthorityAsync(RunnerId);
        return active is not null
            && string.Equals(
                active.CredentialId,
                presentedAuthority.CredentialId,
                StringComparison.Ordinal);
    }

    private async Task ReceiveAdministrativeRemovalReminderAsync()
    {
        string? removalId = null;
        await _lifecycleGate.WaitAsync();
        try
        {
            var removal = _state.State?.AdministrativeRemoval;
            if (removal is null || removal.Phase == RunnerAdministrativeRemovalPhase.Completed)
            {
                await RemoveAdministrativeRemovalReminderCoreAsync();
                return;
            }
            removalId = removal.RemovalId;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await ContinueAdministrativeRemovalAsync(removalId);
    }

    private Task EnsureAdministrativeRemovalReminderAsync() =>
        this.RegisterOrUpdateReminder(
            AdministrativeRemovalReminderName,
            AdministrativeRemovalRetryInterval,
            AdministrativeRemovalRetryInterval);

    private async Task EnsureAdministrativeRemovalReminderIfPendingAsync(string removalId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var current = _state.State?.AdministrativeRemoval;
            if (current is not null
                && current.Phase != RunnerAdministrativeRemovalPhase.Completed
                && string.Equals(current.RemovalId, removalId, StringComparison.Ordinal))
            {
                await EnsureAdministrativeRemovalReminderAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RemoveAdministrativeRemovalReminderIfSafeAsync(string? completedRemovalId = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var current = _state.State?.AdministrativeRemoval;
            if (current is not null
                && (current.Phase != RunnerAdministrativeRemovalPhase.Completed
                    || completedRemovalId is not null
                        && !string.Equals(current.RemovalId, completedRemovalId, StringComparison.Ordinal)))
            {
                return;
            }
            await RemoveAdministrativeRemovalReminderCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RemoveAdministrativeRemovalReminderCoreAsync()
    {
        var reminder = await this.GetReminder(AdministrativeRemovalReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }
}
