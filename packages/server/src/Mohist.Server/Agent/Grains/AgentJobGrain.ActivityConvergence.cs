using Mohist.Server.Runner.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    public async Task<bool> ApplyActivityConvergenceAsync(AgentJobActivityConvergence command)
    {
        await HydrateAsync();
        ArgumentNullException.ThrowIfNull(command);

        if (!IsApplicableActivityConvergence(command))
            return false;

        if (State.ActivitySettlement is not null || IsTerminal)
            return false;

        var settledAt = _timeProvider.GetUtcNow();
        var reason = State.FailureReason
            ?? $"activity-converged:{command.Observation}";

        if (State.TerminalLogOwnership is null
            && !string.IsNullOrWhiteSpace(State.RunnerId)
            && !string.IsNullOrWhiteSpace(State.WorkId))
        {
            State.TerminalLogOwnership = new AgentJobTerminalLogOwnership(
                TerminalLogOwnerKinds.AgentJob,
                Key,
                State.WorkId,
                State.RunnerId);
        }

        State.Status = AgentJobStatus.Unknown;
        State.FailureReason = reason;
        State.RecoveryDeadlineAt = null;
        State.RecoveryFailureCategory = null;
        State.RunningSince = null;
        State.TerminalAt = settledAt;
        State.TerminalResult = new AgentJobTerminalResult(
            AgentJobStatus.Unknown,
            reason,
            null,
            null,
            reason,
            null,
            Model: State.Input?.Model ?? State.RoutedPlan?.Model,
            Variant: State.Input?.Variant ?? State.RoutedPlan?.Variant,
            ReasoningEffort: State.Input?.ReasoningEffort ?? State.RoutedPlan?.ReasoningEffort);
        State.ActivitySettlement = new AgentJobActivitySettlement(
            command.SessionId,
            State.Input!.InitialInputId!,
            State.Input.InitialTurnId!,
            command.Observation,
            command.ContextGeneration,
            command.BindingEpoch,
            command.SettledTurnIds.ToArray(),
            command.SettledJobIds.ToArray(),
            command.SupersededOperationIds.ToArray(),
            command.RecordedAt,
            settledAt);

        // Session already committed the superseded initial Turn with the
        // convergence event. Clearing this opposite-direction obligation is
        // what prevents a Session -> Job -> Session callback cycle.
        State.PendingInitialTurnTerminalDelivery = null;
        StageTerminalDeliveryEvent(
            AgentJobStatus.Unknown, reason, null, reason, "unknown", null, null);
        StageWorkflowTerminalEvent(
            AgentJobStatus.Unknown, reason, null, reason, "unknown", null, null, null);
        StageSubagentTerminalEvent(AgentJobStatus.Unknown);
        DisposeJobTimeoutTimer();

        try
        {
            _reportPersistenceFailures.BeforeActivitySettlementReminder(Key);
            await EnsureRecoveryReminderAsync();
            _reportPersistenceFailures.BeforeActivitySettlementPersist(Key);
            await PersistAsync();
        }
        catch
        {
            // Registration and persistence are one acknowledgement boundary.
            // Reloading prevents either failure from leaving a terminal cache
            // over an unchanged authoritative ledger row.
            _hydrated = false;
            await HydrateAsync();
            throw;
        }

        _terminalCompletion.TrySetResult(State.TerminalResult);
        if (State.PendingTerminalDeliveryEvent is not null)
            await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
        if (State.PendingWorkflowTerminalEvent is not null)
            await EmitWorkflowTerminalEventAsync(State.PendingWorkflowTerminalEvent);
        if (State.PendingSubagentTerminalEvent is not null)
            await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);

        _log.LogInformation(
            "AgentJob {Id} finalized Unknown from Session activity convergence ({Observation}, generation={ContextGeneration}, bindingEpoch={BindingEpoch})",
            Key,
            command.Observation,
            command.ContextGeneration,
            command.BindingEpoch);
        return true;
    }

    private bool IsApplicableActivityConvergence(AgentJobActivityConvergence command)
    {
        var input = State.Input;
        return input is not null
            && !string.IsNullOrWhiteSpace(input.InitialInputId)
            && !string.IsNullOrWhiteSpace(input.InitialTurnId)
            && string.Equals(input.AgentSessionId, command.SessionId, StringComparison.Ordinal)
            && command.ContextGeneration >= 1
            && command.BindingEpoch >= 0
            && command.Observation is (RunnerSessionActivityObservations.Idle
                or RunnerSessionActivityObservations.UnknownToRunner)
            && command.SettledJobIds is not null
            && command.SettledTurnIds is not null
            && command.SupersededOperationIds is not null
            && command.SettledJobIds.Contains(Key, StringComparer.Ordinal)
            && command.SettledTurnIds.Contains(input.InitialTurnId, StringComparer.Ordinal);
    }
}
