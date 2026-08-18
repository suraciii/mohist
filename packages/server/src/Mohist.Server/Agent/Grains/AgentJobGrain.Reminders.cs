namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;

        await HydrateAsync();

        if (State.PendingSessionInterruptionDeliveries is { Count: > 0 })
            await DeliverPendingSessionInterruptionAsync();

        if (IsTerminal)
        {
            await DeliverPendingSessionInterruptionAsync();
            await TryReleaseConcurrencyPermitAsync();
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingFailureEvent is not null)
                await EmitFailureEventAsync(State.PendingFailureEvent);
            if (State.PendingTerminalDeliveryEvent is not null)
                await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
            if (State.PendingSubagentTerminalEvent is not null)
                await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
            if (State.PendingUpdateInterruptionEvent is not null)
                await EmitUpdateInterruptionEventAsync(State.PendingUpdateInterruptionEvent);
            if (State.PendingSessionClose is null
                && State.PendingFailureEvent is null
                && State.PendingTerminalDeliveryEvent is null
                && State.PendingSubagentTerminalEvent is null
                && State.PendingUpdateInterruptionEvent is null
                && State.PendingSessionInterruptionDeliveries is not { Count: > 0 }
                && !State.ConcurrencyReleasePending)
            {
                await UnregisterSelfAsync(reminderName);
                return;
            }
            return;
        }

        if (State.Input is null && State.RoutedPlan is null)
        {
            await UnregisterSelfAsync(reminderName);
            return;
        }

        if (State.RoutedPlan is not null && State.Input is null && !State.RunnerAccepted)
        {
            await AdvancePreparedLaunchAsync();
            return;
        }

        if (State.Status == AgentJobStatus.Unknown)
        {
            if (await FailRecoveringJobIfDueAsync())
                return;

            if (State.PendingInitialTurnTerminalDelivery is { } pending)
                await DeliverInitialTurnTerminalAsync(pending);
            if (State.PendingInitialTurnTerminalDelivery is null
                && State.RecoveryDeadlineAt is null)
                await UnregisterSelfAsync(reminderName);
            return;
        }

        if (State.Status == AgentJobStatus.RecoverablyInterrupted)
        {
            if (EnsureUpdateInterruptionDeadline())
                await PersistAsync();
            if (UpdateInterruptionDeadlineExceeded())
            {
                await EnterRecoveryTerminalStateAsync("agent-result-unconfirmed");
                return;
            }

            await DeliverPendingSessionInterruptionAsync();
            if (State.PendingUpdateInterruptionEvent is { } pending)
                await EmitUpdateInterruptionEventAsync(pending);
            // Keep the reminder armed until the receipt deadline even after
            // the interruption event has been durably delivered.
            if (State.PendingUpdateInterruptionEvent is null
                && State.UpdateInterruptionDeadlineAt is null
                && State.PendingSessionInterruptionDeliveries is not { Count: > 0 })
                await UnregisterSelfAsync(reminderName);
            return;
        }

        if (State.Status == AgentJobStatus.Pending)
        {
            await EvaluatePendingAsync();
            return;
        }

        // Non-terminal, non-prepared state: no recoverable obligation.
        await UnregisterSelfAsync(reminderName);
    }
}
