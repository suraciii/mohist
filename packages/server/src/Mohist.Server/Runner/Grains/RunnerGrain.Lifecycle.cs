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
        if (string.IsNullOrWhiteSpace(state.ClosingProcessGeneration))
            state.ClosingProcessGeneration = state.CurrentProcessGeneration;
        _draining = !string.IsNullOrWhiteSpace(state.ClosingProcessGeneration);
    }

    private async Task ReconcileClosingGenerationAsync()
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
            complete = await CloseoutLostAsync(closingGeneration);
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

    private async Task<bool> CloseoutLostAsync(string? processGeneration)
    {
        var workerId = RunnerId;
        var complete = true;
        IReadOnlyList<string> workflowRunIds = [];
        try
        {
            workflowRunIds = await _workflowRuns.FindRunningAssignedToAsync(workerId);
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
            try
            {
                var run = await _workflowRuns.LoadAsync(workflowRunId);
                if (run is null)
                {
                    complete = false;
                    _log.LogWarning(
                        "Runner {RunnerId} could not load Workflow closeout owner {WorkflowRunId}",
                        RunnerId,
                        workflowRunId);
                    continue;
                }

                var active = run.CurrentActiveWorkFor(workerId);
                if (active is null
                    || !string.Equals(active.ProcessGeneration, processGeneration, StringComparison.Ordinal))
                    continue;

                var verdict = await GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId)
                    .FailActiveWorkAsync(workerId, active.WorkId, processGeneration!, "runner-lost");
                if (verdict == WorkReportVerdict.Outstanding)
                    complete = false;
            }
            catch (Exception ex)
            {
                complete = false;
                _log.LogWarning(ex,
                    "Runner {RunnerId} failed to close active Workflow work for run {WorkflowRunId}",
                    RunnerId,
                    workflowRunId);
            }
        }

        IReadOnlyList<AgentJobLedgerRecord> agentJobs = [];
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

        foreach (var record in agentJobs)
        {
            try
            {
                if (string.IsNullOrEmpty(record.WorkId)
                    || !string.Equals(record.ClaimedProcessGeneration, processGeneration, StringComparison.Ordinal))
                    continue;

                var verdict = await GrainFactory.GetGrain<IAgentJobGrain>(record.JobKey)
                    .FailRunnerLostAsync(workerId, record.WorkId, processGeneration!);
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

        return complete;
    }

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
