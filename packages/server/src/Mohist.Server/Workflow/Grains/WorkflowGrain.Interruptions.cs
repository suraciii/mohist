using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    public const string RunnerLossRecoveryReminderName = "runner-loss-recovery";

    private static readonly TimeSpan RunnerLossRecoveryReminderPeriod = TimeSpan.FromDays(1);

    public async Task<ReportAck> InterruptActiveWorkAsync(string workerId, string reason)
    {
        RejectIfRunReloadRequired();
        if (_run is null || string.IsNullOrWhiteSpace(workerId))
            return ReportAck.Stale;

        var reasonCode = string.IsNullOrWhiteSpace(reason) ? "runner-lost" : reason.Trim();
        var now = Now();
        var update = _run.RecordWorkInterruption(
            GrainKey,
            workerId,
            reasonCode,
            now,
            now + _runnerLossRecoveryTimeout);
        if (update == WorkInterruptionUpdate.Rejected)
            return ReportAck.Stale;

        if (update == WorkInterruptionUpdate.Updated)
            await CommitAsync([]);

        await ReconcileRunnerLossRecoveryAsync();
        return ReportAck.Accepted;
    }

    private async Task ReconcileRunnerLossRecoveryAsync(bool removeReminderWhenClear = false)
    {
        if (_run is null)
            return;

        var interruption = _run.CurrentWorkInterruption();
        if (interruption is null)
        {
            // Directly constructed grains used by production-contract tests do
            // not have an Orleans reminder registry. There is no persisted
            // interruption to reconcile on that activation, so cleanup is
            // only requested after a report cleared an armed interruption.
            if (removeReminderWhenClear)
                await RemoveRunnerLossRecoveryReminderAsync();
            return;
        }

        var now = Now();
        if (interruption.RecoveryDeadlineAt > now)
        {
            await EnsureRunnerLossRecoveryReminderAsync(interruption.RecoveryDeadlineAt);
            return;
        }

        var events = _run.FailInterruptedWorkIfDue(now);
        if (events.Count == 0)
            return;

        await CommitAsync(events);
        await RemoveRunnerLossRecoveryReminderAsync();
    }

    protected virtual Task EnsureRunnerLossRecoveryReminderAsync(DateTimeOffset deadline)
    {
        var due = deadline - Now();
        if (due <= TimeSpan.Zero)
            due = TimeSpan.FromMilliseconds(1);

        return this.RegisterOrUpdateReminder(
            RunnerLossRecoveryReminderName,
            due,
            RunnerLossRecoveryReminderPeriod);
    }

    protected virtual async Task RemoveRunnerLossRecoveryReminderAsync()
    {
        var reminder = await this.GetReminder(RunnerLossRecoveryReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }
}
