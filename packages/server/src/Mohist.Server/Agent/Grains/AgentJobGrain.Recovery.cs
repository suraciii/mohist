using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private async Task<AgentJobReportResult> ReportUnknownResultAsync(WorkResult result)
    {
        // A restarted Runner proves only that this physical dispatch was
        // fenced locally without a durable result. Preserve that fact as
        // Unknown; never route it through terminal failure.
        var reason = string.IsNullOrWhiteSpace(result.Message)
            ? AgentJobFailureReasons.RunnerUnavailable
            : result.Message;
        await EnterUnknownStateAsync(reason);
        return new AgentJobReportResult(true, "unknown");
    }

    private readonly TimeSpan _runnerLossRecoveryTimeout;

    private bool IsRecovering => State.Status == AgentJobStatus.Unknown
        && State.RecoveryDeadlineAt is { } deadline
        && deadline > _timeProvider.GetUtcNow();

    public Task MarkUnknownAsync(string reason) => EnterUnknownStateAsync(reason);

    public Task MarkUnknownAsync(string reason, DateTimeOffset recoveryDeadlineAt) =>
        EnterUnknownStateAsync(reason, recoveryDeadlineAt);

    internal async Task EnterUnknownStateAsync(
        string reason,
        DateTimeOffset? recoveryDeadlineAt = null)
    {
        if (IsTerminal)
            return;

        if (State.Status == AgentJobStatus.Unknown)
        {
            var changed = false;
            if (recoveryDeadlineAt is { } deadline
                && State.RecoveryDeadlineAt is null)
            {
                State.RecoveryDeadlineAt = deadline;
                State.FailureReason = reason;
                changed = true;
            }

            if (EnsureUnknownInitialTurnDelivery(State.FailureReason ?? reason))
                changed = true;
            await EnsureRecoveryReminderAsync();
            if (changed)
                await PersistAsync();
            return;
        }

        var previousStatus = State.Status;
        State.Status = AgentJobStatus.Unknown;
        State.FailureReason = reason;
        State.RecoveryDeadlineAt = recoveryDeadlineAt;
        State.RunningSince = null;
        State.TerminalResult = null;
        State.TerminalAt = null;

        DisposeJobTimeoutTimer();

        EnsureUnknownInitialTurnDelivery(reason);
        StageTerminalDeliveryEvent(AgentJobStatus.Unknown, reason, null, reason, "unknown", null, null);
        await EnsureRecoveryReminderAsync();
        await PersistAsync();
        if (State.PendingTerminalDeliveryEvent is not null)
            await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);

        _log.LogInformation(
            "AgentJob {Id} unknown: previous={Previous}, reason={Reason}, recoveryDeadlineAt={RecoveryDeadlineAt}",
            Key, previousStatus, reason, recoveryDeadlineAt);
    }

    private async Task<bool> FailRecoveringJobIfDueAsync()
    {
        if (State.Status != AgentJobStatus.Unknown
            || State.RecoveryDeadlineAt is not { } deadline
            || _timeProvider.GetUtcNow() < deadline)
        {
            return false;
        }

        var reason = State.FailureReason ?? AgentJobFailureReasons.RunnerLost;
        await EnterTerminalStateAsync(
            AgentJobStatus.Failed,
            exitCode: 1,
            failureReason: reason,
            failureCategory: reason,
            pendingReason: reason,
            message: reason,
            output: null,
            artifactUploadIds: null,
            terminalExitCode: 1);
        return true;
    }

    private async Task<bool> IsRunnerAwayAsync()
    {
        if (string.IsNullOrWhiteSpace(State.RunnerId))
            return false;

        try
        {
            var runtime = await GrainFactory
                .GetGrain<IRunnerGrain>(State.RunnerId)
                .GetRuntimeStateAsync();
            return runtime.Status != RunnerStatus.Online;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentJob {Id} could not read runner {Runner} during report-timeout reconciliation; treating it as lost",
                Key,
                State.RunnerId);
            return true;
        }
    }

    private static TimeSpan ValidateRunnerLossRecoveryTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.FromMinutes(2))
            throw new InvalidOperationException(
                "AgentJob RunnerLossRecoveryTimeout must be longer than the two-minute runner presence timeout.");
        return timeout;
    }
}
