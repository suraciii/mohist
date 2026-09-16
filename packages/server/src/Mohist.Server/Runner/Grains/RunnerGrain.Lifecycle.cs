using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    private void BeginDurableCloseout()
    {
        var state = _state.State ??= new RunnerState();
        // The generation being closed out is the current one. Recording it even
        // over an older pending obligation keeps the current generation's own
        // claims in scope; the older claims stay in scope as non-authoritative.
        if (!string.IsNullOrWhiteSpace(state.CurrentProcessGeneration))
            state.ClosingProcessGeneration = state.CurrentProcessGeneration;
        _draining = !string.IsNullOrWhiteSpace(state.ClosingProcessGeneration);
    }

    /// <summary>
    /// Completes the recorded generation closeout. <paramref name="admittedGeneration"/>
    /// is the generation a registration is about to make current: work from
    /// any other generation is already lost for the process being admitted, so
    /// it must settle before admission. The presence-driven retry passes no
    /// admitted generation, which keeps the recorded closing generation itself
    /// in scope until it is settled.
    /// </summary>
    private async Task ReconcileClosingGenerationAsync(string? admittedGeneration = null)
    {
        var state = _state.State ??= new RunnerState();
        var closingGeneration = state.ClosingProcessGeneration;
        if (string.IsNullOrWhiteSpace(closingGeneration))
        {
            if (_status == RunnerStatus.Offline)
                await RemovePresenceReminderAsync();
            return;
        }

        var complete = false;
        try
        {
            (complete, _) = await CloseoutLostAsync(
                closingGeneration,
                admittedGeneration ?? state.CurrentProcessGeneration);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Runner {RunnerId} closeout for process generation {ProcessGeneration} will retry",
                RunnerId,
                closingGeneration);
        }

        if (!complete)
        {
            await EnsurePresenceReminderAsync();
            return;
        }

        state.ClosingProcessGeneration = null;
        _draining = !string.IsNullOrWhiteSpace(state.PendingProcessGeneration)
            || !string.IsNullOrWhiteSpace(state.UpdateInterruptFence?.PendingId);
        try
        {
            await PersistAsync();
        }
        catch (Exception ex)
        {
            state.ClosingProcessGeneration = closingGeneration;
            _draining = true;
            _log.LogWarning(ex,
                "Runner {RunnerId} could not persist closeout completion for process generation {ProcessGeneration}",
                RunnerId,
                closingGeneration);
            await EnsurePresenceReminderAsync();
            return;
        }

        if (_status == RunnerStatus.Offline)
            await RemovePresenceReminderAsync();
        else
            await EnsurePresenceReminderAsync();
    }

    /// <summary>
    /// Settles active Workflow work that no longer belongs to the Runner's
    /// authoritative process generation. Workflow membership comes from
    /// active-work ownership: the run's own status never exempts a claim,
    /// because a paused run keeps an executing Action on purpose and only the
    /// claim generation tells whether a live process still owns it.
    /// <paramref name="closingGeneration"/> is the generation recorded as lost,
    /// which also covers the case where it is still the Runner's current
    /// generation (presence expiry and unregister). The returned unsettled
    /// claim generation lets the caller keep the obligation when the owner
    /// could not decide yet.
    /// </summary>
    private async Task<(bool Complete, string? UnsettledClaimGeneration)> CloseoutLostAsync(
        string? closingGeneration,
        string? authoritativeGeneration)
    {
        var workerId = RunnerId;
        var complete = true;
        string? unsettledClaimGeneration = null;
        IReadOnlyList<string> workflowRunIds = [];
        try
        {
            workflowRunIds = await _workflowRuns.FindActiveWorkOwnersAssignedToAsync(workerId);
        }
        catch (Exception ex)
        {
            complete = false;
            _log.LogWarning(ex,
                "Runner {RunnerId} could not discover Workflow closeout owners",
                RunnerId);
        }

        foreach (var workflowRunId in workflowRunIds)
        {
            string? claimGeneration = null;
            var settled = true;
            try
            {
                var run = await _workflowRuns.LoadAsync(workflowRunId);
                if (run is null)
                {
                    settled = false;
                    _log.LogWarning(
                        "Runner {RunnerId} could not load Workflow closeout owner {WorkflowRunId}",
                        RunnerId,
                        workflowRunId);
                }
                else
                {
                    var active = run.CurrentActiveWorkFor(workerId);
                    claimGeneration = active?.ProcessGeneration;
                    if (!string.IsNullOrEmpty(claimGeneration)
                        && IsLostClaim(claimGeneration, closingGeneration, authoritativeGeneration))
                    {
                        var verdict = await GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId)
                            .FailActiveWorkAsync(workerId, active!.WorkId, claimGeneration, "runner-lost");
                        if (verdict == WorkReportVerdict.Outstanding)
                            settled = false;
                    }
                }
            }
            catch (Exception ex)
            {
                settled = false;
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to close active Workflow work for run {WorkflowRunId}",
                    RunnerId,
                    workflowRunId);
            }

            if (settled)
                continue;

            complete = false;
            unsettledClaimGeneration ??= claimGeneration;
        }

        // AgentJob claims settle only under an explicit generation closeout:
        // their ledger is the durable owner record and its recovery is
        // deadline-based, so a claim recording no generation to close out is
        // not an AgentJob closeout trigger.
        IReadOnlyList<AgentJobLedgerRecord> agentJobs = [];
        if (!string.IsNullOrWhiteSpace(closingGeneration))
        {
            try
            {
                agentJobs = await _agentJobStore.ListRunningForRunnerAsync(workerId);
            }
            catch (Exception ex)
            {
                complete = false;
                _log.LogWarning(ex,
                    "Runner {RunnerId} could not discover AgentJob closeout owners",
                    RunnerId);
            }
        }

        foreach (var record in agentJobs)
        {
            try
            {
                if (string.IsNullOrEmpty(record.WorkId)
                    || !string.Equals(record.ClaimedProcessGeneration, closingGeneration, StringComparison.Ordinal))
                    continue;

                var verdict = await GrainFactory.GetGrain<IAgentJobGrain>(record.JobKey)
                    .FailRunnerLostAsync(workerId, record.WorkId, closingGeneration!);
                if (verdict == WorkReportVerdict.Outstanding)
                    complete = false;
            }
            catch (Exception ex)
            {
                complete = false;
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to close AgentJob {JobKey}",
                    RunnerId,
                    record.JobKey);
            }
        }

        return (complete, unsettledClaimGeneration);
    }

    /// <summary>
    /// Settles active work that cannot belong to the Runner's authoritative
    /// process generation. A claim from any other generation belongs to a
    /// process that can neither report nor receive the work again, so it goes
    /// through the same runner-lost closeout as a recorded generation.
    /// Activation and registration are where the Server (re)establishes that
    /// authority, which also makes work orphaned by an earlier closeout
    /// decidable after its closing marker was cleared.
    /// </summary>
    private async Task ReconcileSupersededGenerationAsync()
    {
        var state = _state.State ??= new RunnerState();
        var authoritativeGeneration = state.CurrentProcessGeneration;
        if (string.IsNullOrWhiteSpace(authoritativeGeneration)
            || !string.IsNullOrWhiteSpace(state.ClosingProcessGeneration))
            return;

        var (complete, unsettledClaimGeneration) = await CloseoutLostAsync(
            closingGeneration: null,
            authoritativeGeneration);
        if (complete || unsettledClaimGeneration is null)
            return;

        // The claim could not be settled now. Keep it as the durable closeout
        // obligation so the presence reminder retries this closeout instead of
        // the work waiting for the next activation or registration.
        var wasDraining = _draining;
        state.ClosingProcessGeneration = unsettledClaimGeneration;
        _draining = true;
        try
        {
            await PersistAsync();
        }
        catch (Exception ex)
        {
            state.ClosingProcessGeneration = null;
            _draining = wasDraining;
            _log.LogWarning(ex,
                "Runner {RunnerId} could not record the pending closeout for process generation {ProcessGeneration}",
                RunnerId,
                unsettledClaimGeneration);
            return;
        }

        await EnsurePresenceReminderAsync();
    }

    /// <summary>
    /// The closeout membership rule. A claim is lost when it is the generation
    /// being closed out, or when it is not the generation that is authoritative
    /// for the Runner — either the one being admitted by a registration or the
    /// current one. A claim with no recorded generation is not decidable here,
    /// and an unknown authoritative generation leaves only the recorded closing
    /// generation in scope.
    /// </summary>
    private static bool IsLostClaim(
        string claimGeneration,
        string? closingGeneration,
        string? authoritativeGeneration) =>
        string.Equals(claimGeneration, closingGeneration, StringComparison.Ordinal)
        || (!string.IsNullOrWhiteSpace(authoritativeGeneration)
            && !string.Equals(claimGeneration, authoritativeGeneration, StringComparison.Ordinal));

    private async Task EnsurePresenceReminderAsync()
    {
        var state = _state.State;
        var hasLease = _status == RunnerStatus.Online
            && state?.PresenceLeaseExpiresAt is not null;
        var hasCloseout = !string.IsNullOrWhiteSpace(state?.ClosingProcessGeneration);
        if (!hasLease && !hasCloseout)
            return;

        var due = hasLease
            ? state!.PresenceLeaseExpiresAt!.Value - _timeProvider.GetUtcNow()
            : PresenceCheckInterval;
        if (hasCloseout && due > PresenceCheckInterval)
            due = PresenceCheckInterval;
        if (due <= TimeSpan.Zero)
            due = TimeSpan.FromMilliseconds(1);

        await this.RegisterOrUpdateReminder(
            PresenceReminderName,
            due,
            PresenceCheckInterval);
    }

    private async Task RemovePresenceReminderAsync()
    {
        try
        {
            var reminder = await this.GetReminder(PresenceReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Runner {Id} could not unregister presence reminder", RunnerId);
        }
    }

    public Task<bool> IsPresenceLeaseActiveAsync()
    {
        var expiry = _state.State?.PresenceLeaseExpiresAt;
        return Task.FromResult(
            _status == RunnerStatus.Online
            && expiry is { } value
            && value > _timeProvider.GetUtcNow());
    }

}
