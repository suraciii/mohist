using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    public const string RunnerLossRecoveryReminderName = "runner-loss-recovery";

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RunnerLossRecoveryReminderName, StringComparison.Ordinal))
            return;

        try
        {
            var reminder = await this.GetReminder(RunnerLossRecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            // The existing periodic reminder is the retry trigger. Cleanup must
            // never create Workflow state or another reminder.
            _log.LogWarning(ex,
                "Workflow {Id} could not retire legacy reminder {ReminderName}; a later delivery will retry",
                GrainKey,
                RunnerLossRecoveryReminderName);
        }
    }
}
