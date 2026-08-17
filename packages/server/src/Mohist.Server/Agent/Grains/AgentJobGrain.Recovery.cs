using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

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


    public async Task<RuntimeRecoveryReceiptAcknowledgement> ReceiveRecoveryReceiptAsync(
        RuntimeRecoveryReceipt receipt)
    {
        await HydrateAsync();
        State.AppliedRecoveryReceipts ??= [];
        State.RecoveryAttempts ??= [];

        if (receipt is null || receipt.ValidateContract().Count > 0)
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt?.ReceiptId ?? string.Empty,
                RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                "invalid-recovery-receipt");

        if (!string.Equals(
                receipt.OwnerKind ?? RuntimeRecoveryReceiptOwnerKinds.Workflow,
                RuntimeRecoveryReceiptOwnerKinds.AgentJob,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.AgentJobId, Key, StringComparison.Ordinal))
        {
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                "job-identity-mismatch");
        }

        var requestFingerprint = receipt.RequestFingerprint();
        var prior = State.AppliedRecoveryReceipts.FirstOrDefault(candidate =>
            string.Equals(candidate.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal));
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                return new RuntimeRecoveryReceiptAcknowledgement(
                    receipt.ReceiptId,
                    RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                    "receipt-id-reused");
            }

            // The receipt ledger may have been persisted just before the
            // Session or operation-grain write. Repair both cross-grain
            // obligations before returning the stored answer, so an exact
            // replay cannot leave recovery visibility unresolved.
            if (prior.Status == RuntimeRecoveryReceiptAckStatuses.Accepted)
            {
                await RepairSessionInterruptionForReceiptAsync(receipt);
                if (receipt.Payload?.Type.Trim().Equals(
                        RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    await SettleUpdateOperationWorkAsync(receipt);
                }
                else if (receipt.Payload?.Type.Trim().Equals(
                             RuntimeRecoveryReceiptPayloadTypes.TerminalResult,
                             StringComparison.OrdinalIgnoreCase) == true)
                {
                    await RepairTerminalReceiptOperationAsync(receipt);
                }
            }

            if (State.Status == AgentJobStatus.Pending
                && string.IsNullOrWhiteSpace(State.RunnerId)
                && string.Equals(prior.Reason, "replacement-created", StringComparison.Ordinal))
            {
                await TryAdmitAsync();
            }
            return new RuntimeRecoveryReceiptAcknowledgement(prior.ReceiptId, prior.Status, prior.Reason);
        }

        var payload = receipt.Payload!;
        var payloadType = payload.Type.Trim().ToLowerInvariant();
        if (IsTerminal)
        {
            if (payloadType == RuntimeRecoveryReceiptPayloadTypes.TerminalResult
                && MatchesRecoveryReceiptBinding(receipt)
                && receipt.RecoveryGeneration == State.RecoveryGeneration
                && string.Equals(
                    payload.Fingerprint,
                    RuntimeRecoveryReceiptFingerprint.For(payload.NormalizedTerminalResult!),
                    StringComparison.OrdinalIgnoreCase))
            {
                await RepairSessionInterruptionForReceiptAsync(receipt);
                await RepairTerminalReceiptOperationAsync(receipt);
            }

            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.Stale,
                "job-terminal");
        }

        if (!MatchesRecoveryReceiptBinding(receipt)
            || receipt.RecoveryGeneration != State.RecoveryGeneration)
        {
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                "binding-mismatch");
        }

        if (payloadType == RuntimeRecoveryReceiptPayloadTypes.TerminalResult)
        {
            if (State.Status is not (AgentJobStatus.Running
                or AgentJobStatus.Unknown
                or AgentJobStatus.RecoverablyInterrupted))
            {
                return new RuntimeRecoveryReceiptAcknowledgement(
                    receipt.ReceiptId,
                    RuntimeRecoveryReceiptAckStatuses.Stale,
                    "job-not-reportable");
            }

            var terminalResult = payload.NormalizedTerminalResult!;
            if (!string.Equals(
                    payload.Fingerprint,
                    RuntimeRecoveryReceiptFingerprint.For(terminalResult),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeRecoveryReceiptAcknowledgement(
                    receipt.ReceiptId,
                    RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                    "result-fingerprint-mismatch");
            }

            await ApplyRecoveryTerminalResultAsync(terminalResult);
            State.AppliedRecoveryReceipts.Add(new AppliedRuntimeRecoveryReceipt(
                receipt.ReceiptId,
                requestFingerprint,
                RuntimeRecoveryReceiptAckStatuses.Accepted));
            await PersistAsync();
            await RepairTerminalReceiptOperationAsync(receipt);
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.Accepted);
        }

        if (State.Status != AgentJobStatus.RecoverablyInterrupted
            || !string.Equals(State.UpdateOperationId, payload.UpdateOperationId, StringComparison.Ordinal))
        {
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                "update-fence-mismatch");
        }

        var operation = await GrainFactory
            .GetGrain<IRunnerUpdateOperationGrain>(receipt.RunnerId)
            .GetAsync(payload.UpdateOperationId!);
        var fencedWork = operation?.AffectedWorks.SingleOrDefault(work =>
            string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
            && string.Equals(work.OwnerId, Key, StringComparison.Ordinal)
            && string.Equals(work.WorkId, receipt.WorkId, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(work.TaskRunId));
        if (fencedWork is null
            || fencedWork.Status is not (RunnerUpdateWorkStatus.Marked or RunnerUpdateWorkStatus.Settled))
        {
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.RejectedMismatch,
                "update-fence-missing");
        }

        var canContinue = CanContinueAfterUpdateInterruption();
        var reason = canContinue ? "replacement-created" : "cannot-continue";
        if (canContinue)
            await AllocateRecoveryAttemptAsync(receipt.WorkId);
        else
            await EnterRecoveryTerminalStateAsync("update-interrupted-cannot-continue");

        State.AppliedRecoveryReceipts.Add(new AppliedRuntimeRecoveryReceipt(
            receipt.ReceiptId,
            requestFingerprint,
            RuntimeRecoveryReceiptAckStatuses.Accepted,
            reason));
        await PersistAsync();
        await DeliverPendingSessionInterruptionAsync();
        await SettleUpdateOperationWorkAsync(receipt);
        if (canContinue)
            await TryAdmitAsync();

        return new RuntimeRecoveryReceiptAcknowledgement(
            receipt.ReceiptId,
            RuntimeRecoveryReceiptAckStatuses.Accepted,
            reason);
    }

    private async Task MarkRecoverySettledAsync()
    {
        if (State.UpdateOperationId is not { } operationId
            || string.IsNullOrWhiteSpace(State.InterruptedWorkId))
            return;

        var original = State.RecoveryAttempts.FirstOrDefault(attempt =>
            string.Equals(attempt.WorkId, State.InterruptedWorkId, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(original?.RunnerId))
            return;

        await _grains
            .GetGrain<IRunnerUpdateOperationGrain>(original.RunnerId)
            .MarkRecoverySettledAsync(
                operationId,
                WorkDispatchOwnerKinds.AgentJob,
                Key,
                State.InterruptedWorkId!,
                taskRunId: null);
    }

    private async Task RepairTerminalReceiptOperationAsync(RuntimeRecoveryReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(State.UpdateOperationId)
            || string.IsNullOrWhiteSpace(State.InterruptedWorkId))
        {
            return;
        }

        if (State.RecoveryGeneration > 0)
            await MarkRecoverySettledAsync();

        await _grains
            .GetGrain<IRunnerUpdateOperationGrain>(receipt.RunnerId)
            .MarkReceiptAckedAsync(
                RuntimeRecoveryReceiptOwnerKinds.AgentJob,
                Key,
                receipt.WorkId,
                taskRunId: null);
    }

    private void RecordInterruptedAttempt(string? workId, DateTimeOffset recordedAt)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        State.RecoveryAttempts ??= [];
        var index = State.RecoveryAttempts.FindIndex(attempt =>
            attempt.RecoveryGeneration == State.RecoveryGeneration
            && string.Equals(attempt.WorkId, workId, StringComparison.Ordinal));
        var attempt = new AgentJobRecoveryAttempt(
            State.RecoveryGeneration,
            workId,
            State.RunnerId,
            State.Input?.AgentSessionId,
            State.Input?.InitialInputId,
            State.Input?.InitialTurnId,
            State.Input?.Runtime,
            State.RuntimeSessionId,
            AgentJobStatus.RecoverablyInterrupted,
            recordedAt);
        if (index < 0)
            State.RecoveryAttempts.Add(attempt);
        else
            State.RecoveryAttempts[index] = attempt;
    }


    private TimeSpan ResolveUpdateInterruptionTimeout() =>
        _options.UpdateInterruptionTimeout > TimeSpan.Zero
            ? _options.UpdateInterruptionTimeout
            : TimeSpan.FromMinutes(5);

    private bool EnsureUpdateInterruptionDeadline()
    {
        if (State.Status != AgentJobStatus.RecoverablyInterrupted
            || State.UpdateInterruptionDeadlineAt is not null)
            return false;

        State.UpdateInterruptionDeadlineAt = _timeProvider.GetUtcNow() + ResolveUpdateInterruptionTimeout();
        return true;
    }

    private bool UpdateInterruptionDeadlineExceeded() =>
        State.Status == AgentJobStatus.RecoverablyInterrupted
        && State.UpdateInterruptionDeadlineAt is { } deadline
        && deadline <= _timeProvider.GetUtcNow();

    private async Task DeliverPendingSessionInterruptionAsync()
    {
        State.PendingSessionInterruptionDeliveries ??= [];
        while (State.PendingSessionInterruptionDeliveries.Count > 0)
        {
            var pending = State.PendingSessionInterruptionDeliveries[0];
            if (!await ApplySessionInterruptionAsync(pending.SessionId, pending.Transition))
                break;

            State.PendingSessionInterruptionDeliveries.RemoveAt(0);
            await PersistAsync();
        }
    }

    private async Task RepairSessionInterruptionForReceiptAsync(RuntimeRecoveryReceipt receipt)
    {
        await DeliverPendingSessionInterruptionAsync();
        if (State.Interruption is not { } transition)
            return;

        await ApplySessionInterruptionAsync(State.Input?.AgentSessionId, transition);
    }

    private async Task<bool> ApplySessionInterruptionAsync(
        string? sessionId,
        AgentWorkInterruptionTransition transition)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return true;
        var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        if (await session.GetAsync() is null) return false;
        await session.ApplyInterruptionAsync(transition);
        return true;
    }

    private async Task EnsureRecoveryReminderAsync()
    {
        await this.RegisterOrUpdateReminder(
            RecoveryReminderName,
            RecoveryReminderDue,
            RecoveryReminderPeriod);
    }
}
