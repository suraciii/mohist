using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    public const string AgentResultSettlementReminderName = "agent-result-settlement";

    private static readonly TimeSpan AgentResultSettlementReminderPeriod = TimeSpan.FromDays(1);

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (string.Equals(reminderName, RunnerLossRecoveryReminderName, StringComparison.Ordinal))
        {
            RejectIfRunReloadRequired();
            await ReconcileRunnerLossRecoveryAsync();
            return;
        }

        if (!string.Equals(reminderName, AgentResultSettlementReminderName, StringComparison.Ordinal))
            return;

        RejectIfRunReloadRequired();
        if (_run is null)
            return;

        await ReconcileAgentResultSettlementAsync();
    }

    private async Task ReconcileAgentResultSettlementAsync()
    {
        if (_run is null)
            return;

        if (_run.Status == WorkflowRunStatus.Stopped
            && _run.FindCancelledAgentResultSettlementTask() is not null)
        {
            await ReconcileAgentResultSettlementCleanupAsync();
            return;
        }

        var unresolved = _run.FindUnresolvedAgentResultSettlementTask();
        var settlement = unresolved?.Task.AgentResultSettlement;
        if (settlement is null)
        {
            await ReconcileTerminalAgentResultSettlementAsync();
            return;
        }

        if (settlement.State == AgentResultSettlementState.Unknown)
        {
            if (EnsureSettlementDeadline(settlement))
                await CommitAsync([]);

            await DeleteAgentResultSettlementSnapshotAsync(settlement.WorkId);
            if (settlement.DeadlineAt <= Now())
            {
                await BlockUnresolvedAgentResultIfDueAsync();
                return;
            }

            await EnsureAgentResultSettlementReminderAsync(settlement.DeadlineAt!.Value);
            return;
        }

        if (settlement.State == AgentResultSettlementState.Blocked)
        {
            await DeleteAgentResultSettlementSnapshotAsync(settlement.WorkId);
            await RemoveAgentResultSettlementReminderAsync();
        }
    }

    private async Task BlockUnresolvedAgentResultIfDueAsync()
    {
        if (_run is null)
            return;

        var events = _run.BlockUnresolvedAgentResult(Now());
        if (events.Count > 0)
            await CommitAsync(events);

        var unresolved = _run.FindUnresolvedAgentResultSettlementTask();
        if (unresolved?.Task.AgentResultSettlement?.State == AgentResultSettlementState.Blocked)
            await RemoveAgentResultSettlementReminderAsync();
    }

    private async Task ReconcileAgentResultSettlementCleanupAsync()
    {
        if (_run?.FindCancelledAgentResultSettlementTask() is not { } cancelled)
            return;

        await DeleteAgentResultSettlementSnapshotAsync(cancelled.Task.AgentResultSettlement!.WorkId);
        await RemoveAgentResultSettlementReminderAsync();
        await ReleaseUnresolvedAgentResultSettlementStageLocksAsync(cancelled.Stage);
    }

    private async Task ReconcileTerminalAgentResultSettlementAsync()
    {
        if (_run?.FindTerminalAgentResultSettlementTask() is not { } terminal)
            return;

        await DeleteAgentResultSettlementSnapshotAsync(terminal.Task.AgentResultSettlement!.WorkId);
        await RemoveAgentResultSettlementReminderAsync();
        terminal.Task.AgentResultSettlement = null;
        await CommitAsync([]);
    }

    private bool EnsureSettlementDeadline(AgentResultSettlement settlement)
    {
        if (settlement.DeadlineAt is not null)
            return false;

        settlement.FirstUnknownAt ??= Now();
        settlement.DeadlineAt = settlement.FirstUnknownAt.Value + _agentResultSettlementTimeout;
        return true;
    }

    protected virtual Task EnsureAgentResultSettlementReminderAsync(DateTimeOffset deadline)
    {
        var due = deadline - Now();
        if (due <= TimeSpan.Zero)
            due = TimeSpan.FromMilliseconds(1);

        return this.RegisterOrUpdateReminder(
            AgentResultSettlementReminderName,
            due,
            AgentResultSettlementReminderPeriod);
    }

    protected virtual async Task RemoveAgentResultSettlementReminderAsync()
    {
        var reminder = await this.GetReminder(AgentResultSettlementReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }

    protected virtual Task DeleteAgentResultSettlementSnapshotAsync(string workId) =>
        DeleteSnapshotBestEffortAsync(workId);

    protected virtual Task ReleaseUnresolvedAgentResultSettlementStageLocksAsync(string stage) =>
        ReleaseStageLocksAsync(stage, "stopped-unresolved-agent-result");
}
