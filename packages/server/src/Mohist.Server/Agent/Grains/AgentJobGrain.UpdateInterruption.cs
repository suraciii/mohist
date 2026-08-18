using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    /// <summary>
    /// Marks the job recoverably interrupted by a runner update operation.
    /// The runner owns the update fence; this grain records the interruption
    /// and emits the update-interruption CloudEvent exactly once so the
    /// workflow (or external observer) can arbitrate one replacement attempt.
    /// Idempotent: re-issuing with the same operation identity re-emits a
    /// pending event; a different identity or a non-running job is refused.
    /// </summary>
    public async Task<bool> MarkUpdateInterruptedAsync(
        string runnerId,
        string workId,
        string updateOperationId)
    {
        await HydrateAsync();

        if (State.Status == AgentJobStatus.RecoverablyInterrupted)
        {
            if (!string.Equals(State.UpdateOperationId, updateOperationId, StringComparison.Ordinal)
                || !string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
                || !string.Equals(State.WorkId, workId, StringComparison.Ordinal))
            {
                return false;
            }

            await DeliverPendingSessionInterruptionAsync();
            if (State.PendingUpdateInterruptionEvent is { } pending)
            {
                await EmitUpdateInterruptionEventAsync(pending);
                if (State.PendingUpdateInterruptionEvent is not null)
                    throw new InvalidOperationException($"AgentJob '{Key}' update interruption event is not committed.");
            }
            return true;
        }

        if (State.Status != AgentJobStatus.Running
            || !string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, workId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(updateOperationId))
        {
            return false;
        }

        var interruptedAt = _timeProvider.GetUtcNow();
        State.Status = AgentJobStatus.RecoverablyInterrupted;
        State.UpdateOperationId = updateOperationId;
        State.InterruptedWorkId = workId;
        State.RecoveryTerminalReason = null;
        State.UpdateInterruptionDeadlineAt = interruptedAt + ResolveUpdateInterruptionTimeout();
        State.RunningSince = null;
        var interruption = new AgentWorkInterruptionTransition(
            AgentWorkInterruptionStates.Interrupted,
            updateOperationId,
            workId,
            null,
            State.RecoveryGeneration,
            State.Input?.InitialTurnId,
            null,
            null,
            "The Runner will deliver a confirmed interruption receipt; a fresh AgentJob dispatch will resume this work.",
            interruptedAt);
        State.InterruptionHistory = AgentWorkInterruptionProjection.Apply(
            State.InterruptionHistory,
            interruption with { State = AgentWorkInterruptionStates.Interrupting }).ToList();
        State.InterruptionHistory = AgentWorkInterruptionProjection.Apply(
            State.InterruptionHistory,
            interruption).ToList();
        State.Interruption = interruption;
        State.PendingUpdateInterruptionEvent = new PendingUpdateInterruptionEvent(
            AgentJobSessionDeliveryIds.UpdateInterruptionEventId(Key, updateOperationId),
            updateOperationId,
            runnerId,
            workId,
            interruptedAt,
            State.RecoveryGeneration);
        QueueSessionInterruptionDelivery(
            State.Input?.AgentSessionId,
            interruption with { State = AgentWorkInterruptionStates.Interrupting });
        QueueSessionInterruptionDelivery(State.Input?.AgentSessionId, interruption);
        DisposeJobTimeoutTimer();
        await EnsureRecoveryReminderAsync();
        await PersistAsync();
        await DeliverPendingSessionInterruptionAsync();
        await EmitUpdateInterruptionEventAsync(State.PendingUpdateInterruptionEvent);
        if (State.PendingUpdateInterruptionEvent is not null)
            throw new InvalidOperationException($"AgentJob '{Key}' update interruption event is not committed.");
        return true;
    }
}
