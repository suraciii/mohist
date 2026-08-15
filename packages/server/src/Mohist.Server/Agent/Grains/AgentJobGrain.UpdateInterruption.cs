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

        State.Status = AgentJobStatus.RecoverablyInterrupted;
        State.UpdateOperationId = updateOperationId;
        State.RunningSince = null;
        State.PendingUpdateInterruptionEvent = new PendingUpdateInterruptionEvent(
            AgentJobSessionDeliveryIds.UpdateInterruptionEventId(Key, updateOperationId),
            updateOperationId,
            runnerId,
            workId,
            _timeProvider.GetUtcNow());
        DisposeJobTimeoutTimer();
        await EnsureRecoveryReminderAsync();
        await PersistAsync();
        await EmitUpdateInterruptionEventAsync(State.PendingUpdateInterruptionEvent);
        if (State.PendingUpdateInterruptionEvent is not null)
            throw new InvalidOperationException($"AgentJob '{Key}' update interruption event is not committed.");
        return true;
    }

    private async Task EmitUpdateInterruptionEventAsync(PendingUpdateInterruptionEvent obligation)
    {
        try
        {
            await _eventStore.AppendAsync(BuildUpdateInterruptionEnvelope(obligation), CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} could not append update interruption event (eventId={EventId}); reminder will retry",
                Key,
                obligation.EventId);
            await EnsureRecoveryReminderAsync();
            return;
        }

        State.PendingUpdateInterruptionEvent = null;
        await PersistAsync();
        _log.LogInformation(
            "AgentJob {Id} emitted {Type} event (eventId={EventId}, operationId={OperationId})",
            Key,
            EventCatalog.ReverseDns.AgentJobUpdateInterrupted,
            obligation.EventId,
            obligation.UpdateOperationId);
    }

    private CloudEvent BuildUpdateInterruptionEnvelope(PendingUpdateInterruptionEvent obligation)
    {
        var extensions = AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan);
        return AgentJobLineage.BuildUpdateInterruptionEnvelope(Key, obligation, extensions);
    }
}