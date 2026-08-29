using Mohist.Server.Contracts;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    internal async Task EnterTerminalStateAsync(
        AgentJobStatus terminalStatus,
        int? exitCode,
        string? failureReason,
        string? failureCategory,
        string? pendingReason,
        string? message,
        string? output,
        string[]? artifactUploadIds,
        int? terminalExitCode,
        string? addTasksJson = null)
    {
        // For a failed report the message is the runner's failure diagnostic and
        // carries the same token risk as the reason; every durable copy below
        // (ledger, events, session turn) must inherit redacted text.
        message = message is null ? null : SlackSecretRedactor.Redact(message);
        failureReason = failureReason is null ? null : SlackSecretRedactor.Redact(failureReason);
        pendingReason = pendingReason is null ? null : SlackSecretRedactor.Redact(pendingReason);
        var pending = BuildPendingSessionClose(terminalStatus, exitCode, failureReason, failureCategory, pendingReason);

        if (IsTerminal)
        {
            State.PendingSessionClose ??= pending;
            if (State.PendingSessionClose is not null
                || State.PendingFailureEvent is not null
                || State.PendingTerminalDeliveryEvent is not null
                || State.PendingWorkflowTerminalEvent is not null
                || State.PendingSubagentTerminalEvent is not null)
                await EnsureRecoveryReminderAsync();
            await PersistAsync();
            await TryReleaseConcurrencyPermitAsync();
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingFailureEvent is not null)
                await EmitFailureEventAsync(State.PendingFailureEvent);
            if (State.PendingTerminalDeliveryEvent is not null)
                await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
            if (State.PendingWorkflowTerminalEvent is not null)
                await EmitWorkflowTerminalEventAsync(State.PendingWorkflowTerminalEvent);
            if (State.PendingSubagentTerminalEvent is not null)
                await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
            return;
        }

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

        State.Status = terminalStatus;
        State.FailureReason = failureReason;
        State.RecoveryDeadlineAt = null;
        State.RunningSince = null;
        State.TerminalAt = _timeProvider.GetUtcNow();
        State.TerminalResult = new AgentJobTerminalResult(
            terminalStatus,
            message,
            output,
            artifactUploadIds,
            failureReason,
            terminalExitCode ?? exitCode,
            Model: State.Input?.Model ?? State.RoutedPlan?.Model,
            Variant: State.Input?.Variant ?? State.RoutedPlan?.Variant,
            ReasoningEffort: State.Input?.ReasoningEffort ?? State.RoutedPlan?.ReasoningEffort);
        State.PendingSessionClose = pending;
        StageTerminalDeliveryEvent(
            terminalStatus,
            message,
            output,
            failureReason,
            failureCategory,
            artifactUploadIds,
            terminalExitCode ?? exitCode);
        StageWorkflowTerminalEvent(
            terminalStatus,
            message,
            output,
            failureReason,
            failureCategory,
            artifactUploadIds,
            terminalExitCode ?? exitCode,
            addTasksJson);
        StageSubagentTerminalEvent(terminalStatus);

        if (terminalStatus == AgentJobStatus.Failed)
        {
            State.PendingFailureEvent = new PendingFailureEvent(
                EventId: AgentJobSessionDeliveryIds.FailureEventId(Key),
                FailureReason: failureReason ?? pendingReason,
                FailureCategory: failureCategory,
                RecordedAt: _timeProvider.GetUtcNow());
        }

        DisposeJobTimeoutTimer();

        State.ConcurrencyGateStatus = terminalStatus == AgentJobStatus.Cancelled
            ? AgentConcurrencyPermitStatus.Cancelled
            : AgentConcurrencyPermitStatus.Terminal;
        State.ConcurrencyReleasePending = State.ConcurrencyPermitId is not null
            || State.ConcurrencyPermitHeld
            || State.ConcurrencyWaiterId is not null;
        await EnsureRecoveryReminderAsync();
        await PersistAsync();
        await TryReleaseConcurrencyPermitAsync();
        _terminalCompletion.TrySetResult(State.TerminalResult);

        _log.LogInformation(
            "AgentJob {Id} terminal: {Status} ({Reason}, category={Category}, deliveryId={DeliveryId})",
            Key, State.Status, SlackSecretRedactor.Redact(State.FailureReason ?? "ok"),
            State.PendingSessionClose?.FailureCategory ?? "-",
            State.PendingSessionClose?.DeliveryId ?? "-");

        await DeliverTerminalToSessionAsync(pending);
        await MarkInitialTurnTerminalAsync(terminalStatus, message, output, failureReason, failureCategory, terminalExitCode ?? exitCode);
        if (State.PendingFailureEvent is not null)
            await EmitFailureEventAsync(State.PendingFailureEvent);
        if (State.PendingTerminalDeliveryEvent is not null)
            await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
        if (State.PendingWorkflowTerminalEvent is not null)
            await EmitWorkflowTerminalEventAsync(State.PendingWorkflowTerminalEvent);
        if (State.PendingSubagentTerminalEvent is not null)
            await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
    }
}
