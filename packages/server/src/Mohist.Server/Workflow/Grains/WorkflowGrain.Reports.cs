using Mohist.Server.Contracts;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    public async Task<ReportAck> BindAgentExecutionAsync(AgentExecutionBinding binding)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        var update = _run.BindAgentExecution(binding);
        if (update == AgentExecutionUpdate.Rejected) return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
            await CommitAsync([]);
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> MarkUpdateInterruptedAsync(
        string taskRunId,
        string workId,
        string runnerId,
        string updateOperationId,
        DateTimeOffset? interruptedAt = null,
        TimeSpan? settlementTimeout = null)
    {
        RejectIfRunReloadRequired();
        if (_run is null)
            return ReportAck.Stale;

        var interruptedAtValue = interruptedAt ?? Now();
        var existingTask = _run.FindTaskForRecoveryReceipt(taskRunId, workId);
        var existingSettlement = existingTask?.AgentResultSettlement;
        var update = _run.MarkUpdateInterrupted(
            taskRunId,
            workId,
            runnerId,
            updateOperationId,
            interruptedAtValue,
            settlementTimeout ?? _agentResultSettlementTimeout);
        if (update == AgentExecutionUpdate.Rejected)
            return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
        {
            var stage = _run.Stages.Single(stage => stage.Tasks.Any(task =>
                string.Equals(task.Id, taskRunId, StringComparison.Ordinal)));
            var settlement = existingTask!.AgentResultSettlement!;
            var transition = new AgentWorkInterruptionTransition(
                AgentWorkInterruptionStates.Interrupted,
                updateOperationId,
                workId,
                taskRunId,
                settlement.RecoveryGeneration,
                existingSettlement?.AgentTurnId,
                null,
                null,
                "The Runner will deliver a confirmed interruption receipt; the replacement dispatch will then resume this work.",
                interruptedAtValue);
            existingTask.AgentInterruption = transition;
            await CommitAsync([
                new AgentTaskInterruptionLifecycleChanged(
                    stage.Id,
                    taskRunId,
                    transition with { State = AgentWorkInterruptionStates.Interrupting }),
                new AgentTaskUpdateInterrupted(stage.Id, taskRunId, workId, updateOperationId),
                new AgentTaskInterruptionLifecycleChanged(stage.Id, taskRunId, transition)]);
            await ApplySessionInterruptionAsync(
                settlement.AgentSessionId,
                transition with { State = AgentWorkInterruptionStates.Interrupting });
            await ApplySessionInterruptionAsync(settlement.AgentSessionId, transition);
        }

        // A receipt can arrive at any time before this deadline. If it does
        // not, the existing settlement reminder converts the fenced work to
        // the explicit agent-result-unconfirmed blocked state.
        await ReconcileAgentResultSettlementAsync();
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> MarkUpdateStopFailureAsync(
        string taskRunId,
        string workId,
        string runnerId,
        string updateOperationId,
        string failure)
    {
        RejectIfRunReloadRequired();
        if (_run is null || string.IsNullOrWhiteSpace(failure))
            return ReportAck.Stale;

        var task = _run.FindTaskForRecoveryReceipt(taskRunId, workId);
        var settlement = task?.AgentResultSettlement;
        if (task is null
            || task.Status != TaskRunStatus.Running
            || settlement is null
            || settlement.State != AgentResultSettlementState.RecoverablyInterrupted
            || !string.Equals(settlement.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(settlement.TaskRunId, taskRunId, StringComparison.Ordinal)
            || !string.Equals(settlement.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(settlement.UpdateOperationId, updateOperationId, StringComparison.Ordinal)
            || task.AgentInterruption is null)
        {
            return ReportAck.Stale;
        }

        var transition = task.AgentInterruption with { StopFailure = failure, RecordedAt = Now() };
        task.AgentInterruption = transition;
        var stage = _run.Stages.Single(candidate => candidate.Tasks.Contains(task));
        await CommitAsync([
            new AgentTaskInterruptionLifecycleChanged(stage.Id, task.Id, transition)
        ]);
        await ApplySessionInterruptionAsync(settlement.AgentSessionId, transition);
        return ReportAck.Accepted;
    }

    public Task<bool> CanStartAgentCleanupAsync(AgentExecutionBinding binding)
    {
        RejectIfRunReloadRequired();
        return Task.FromResult(_run?.CanStartAgentCleanup(binding) == true);
    }

    public async Task<ReportAck> ObserveAgentExecutionAsync(AgentExecutionObservation observation)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        // Reconcile a due settlement before applying a late observation. The
        // deadline owns the race, so an observation cannot update a result
        // after the server has already decided it is blocked.
        await ReconcileAgentResultSettlementIfDueAsync();

        var existing = _run.FindAgentResultSettlementTask(observation.Binding);
        var wasAwaitingResult = existing?.Task.AgentResultSettlement?.State == AgentResultSettlementState.AwaitingResult;
        var update = _run.ObserveAgentExecution(observation, Now(), _agentResultSettlementTimeout);
        if (update == AgentExecutionUpdate.Rejected) return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
        {
            var settlement = _run.FindAgentResultSettlementTask(observation.Binding)?.Task.AgentResultSettlement;
            IReadOnlyList<WorkflowEvent> events = wasAwaitingResult && settlement?.DeadlineAt is { } deadline
                ? [new AgentTaskResultUnconfirmed(
                    existing!.Stage,
                    existing.Task.Id,
                    settlement.WorkId,
                    observation.ReasonCode,
                    deadline)]
                : [];
            await CommitAsync(events);
        }

        await ReconcileAgentResultSettlementAsync();
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> ObserveAgentResultUnknownAsync(
        string workerId,
        string taskRunId,
        string workId,
        string reasonCode,
        string? message = null)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        await ReconcileAgentResultSettlementIfDueAsync();

        var attempt = _run.FindReportableTaskAttempt(taskRunId, workId, workerId);
        var wasAwaitingResult = attempt?.SettlementState == AgentResultSettlementState.AwaitingResult;
        var observedAt = Now();
        var update = attempt is not null
            ? _run.ObserveAgentResultUnknown(taskRunId, workId, workerId, reasonCode, message, observedAt, _agentResultSettlementTimeout)
            : AgentExecutionUpdate.Rejected;
        if (update == AgentExecutionUpdate.Rejected) return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
        {
            await CommitAsync(wasAwaitingResult && attempt is not null
                ? [new AgentTaskResultUnconfirmed(
                    attempt.Stage,
                    attempt.TaskRunId,
                    attempt.WorkId,
                    reasonCode,
                    observedAt + _agentResultSettlementTimeout)]
                : []);
        }

        await ReconcileAgentResultSettlementAsync();
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> ObserveAgentRunnerDisconnectedAsync(string workerId)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        await ReconcileAgentResultSettlementIfDueAsync();

        var active = _run.CurrentActiveWorkFor(workerId);
        var taskRunId = active?.TaskRunId;
        var task = taskRunId is null
            ? null
            : _run.Stages.SelectMany(stage => stage.Tasks).SingleOrDefault(candidate => candidate.Id == taskRunId);
        var wasAwaitingResult = task?.AgentResultSettlement?.State == AgentResultSettlementState.AwaitingResult;
        var update = _run.ObserveAgentRunnerDisconnected(workerId, Now(), _agentResultSettlementTimeout);
        if (update == AgentExecutionUpdate.Rejected) return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
        {
            var deadline = task?.AgentResultSettlement?.DeadlineAt;
            await CommitAsync(wasAwaitingResult && active is not null && task is not null && deadline is { } due
                ? [new AgentTaskResultUnconfirmed(active.Item.Stage, task.Id, active.WorkId, "runner-disconnected", due)]
                : []);
        }

        await ReconcileAgentResultSettlementAsync();
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> FailActiveWorkAsync(string workerId, string message)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return ReportAck.Stale;
        var activeWork = _run.CurrentActiveWorkFor(workerId);
        if (activeWork is null) return ReportAck.Stale;
        if (activeWork.IsTask && _run.CurrentStage().RunningTask?.AgentResultSettlement is not null)
            return ReportAck.Stale;

        var now = Now();
        IReadOnlyList<WorkflowEvent> events;
        string? terminalWorkId = null;
        if (activeWork.IsTask)
        {
            terminalWorkId = activeWork.WorkId;
            events = _run.FailTask(new TaskResult("failed", message), now);
        }
        else if (activeWork.IsChecks)
        {
            events = _run.FailRunningChecks(message, now);
        }
        else
        {
            return ReportAck.Stale;
        }

        if (events.Count == 0) return ReportAck.Stale;

        await CommitAsync(events);
        if (terminalWorkId is not null)
            await DeleteSnapshotBestEffortAsync(terminalWorkId);
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> AbandonActiveWorkAsync(string workerId, string workId, string reason)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return ReportAck.Stale;

        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null) return ReportAck.Stale;
        if (activeWork.IsTask && _run.CurrentStage().RunningTask?.AgentResultSettlement is not null)
            return ReportAck.Stale;

        await _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);

        IReadOnlyList<WorkflowEvent> events;
        if (_run.Status == WorkflowRunStatus.Paused)
        {
            if (activeWork.IsTask)
            {
                if (!_run.RequeueTaskAfterPausedStop(workId, workerId))
                    return ReportAck.Stale;
            }
            else if (activeWork.IsChecks)
            {
                _workLifecycle.RequeueRunningChecks(_run);
            }
            else
            {
                return ReportAck.Stale;
            }

            events = [];
        }
        else if (_run.Status == WorkflowRunStatus.Stopped)
        {
            if (activeWork.IsTask)
            {
                events = _run.FailTaskForStopped(reason, Now());
            }
            else if (activeWork.IsChecks)
            {
                _workLifecycle.RequeueRunningChecks(_run);
                events = [];
            }
            else
            {
                return ReportAck.Stale;
            }
        }
        else if (activeWork.IsTask)
        {
            events = _run.FailTask(new TaskResult("failed", reason), Now());
        }
        else if (activeWork.IsChecks)
        {
            events = _run.FailRunningChecks(reason, Now());
        }
        else
        {
            return ReportAck.Stale;
        }

        await CommitAsync(events);
        if (activeWork.IsTask)
            await DeleteSnapshotBestEffortAsync(workId);
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> RejectActiveWorkDispatchAsync(string workerId, string workId, ExecutionError error)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return ReportAck.Stale;
        var activeWork = _run.FindReportableWork(workId, workerId);
        if (activeWork is null || !activeWork.IsTask) return ReportAck.Stale;

        var task = _run.CurrentStage().RunningTask;
        if (task is not null) task.Error = error;

        var events = _run.FailTask(new TaskResult("failed", error.Message, error), Now());
        if (events.Count == 0) return ReportAck.Stale;

        _log.LogWarning(
            "run {run} rejected dispatch for work {work}: {code} {reason}",
            GrainKey, workId, error.Code, error.Message);
        await CommitAsync(events);
        await DeleteSnapshotBestEffortAsync(workId);
        return ReportAck.Accepted;
    }

    public async Task<RuntimeRecoveryReceiptAcknowledgement> ReceiveRecoveryReceiptAsync(RuntimeRecoveryReceipt receipt)
    {
        RejectIfRunReloadRequired();
        if (_run is null)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt?.ReceiptId ?? string.Empty, RuntimeRecoveryReceiptAckStatuses.Stale, "missing-workflow");

        if (receipt is null || receipt.ValidateContract().Count > 0)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt?.ReceiptId ?? string.Empty, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "invalid-recovery-receipt");

        var requestFingerprint = receipt.RequestFingerprint();
        var prior = _run.AppliedRecoveryReceipts.FirstOrDefault(candidate =>
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

            // The Workflow commit is authoritative. Repair the operation
            // ledger on an acknowledgement replay in case the process failed
            // after committing the replacement but before marking the fence
            // entry settled.
            if (receipt.Payload?.Type.Trim().Equals(
                    RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted,
                    StringComparison.OrdinalIgnoreCase) == true
                && receipt.Payload.UpdateOperationId is { } replayOperationId)
            {
                await SettleUpdateOperationWorkAsync(receipt, replayOperationId);
            }

            return new RuntimeRecoveryReceiptAcknowledgement(prior.ReceiptId, prior.Status, prior.Reason);
        }

        if (!string.Equals(receipt.WorkflowRunId, GrainKey, StringComparison.Ordinal))
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "workflow-identity-mismatch");

        var task = _run.FindTaskForRecoveryReceipt(receipt.TaskRunId, receipt.WorkId);
        if (task is null)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.Stale, "work-not-found");
        if (_run.Status.IsTerminal() || task.Status != TaskRunStatus.Running)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.Stale, "work-terminal");

        var settlement = task.AgentResultSettlement;
        if (settlement is null)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "settlement-missing");
        if (!MatchesReceiptBinding(settlement, receipt)
            || settlement.RecoveryGeneration != receipt.RecoveryGeneration)
        {
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "binding-mismatch");
        }

        var payload = receipt.Payload!;
        var payloadType = payload.Type.Trim().ToLowerInvariant();
        if (payloadType == RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted)
        {
            if (settlement.State != AgentResultSettlementState.RecoverablyInterrupted
                || !string.Equals(settlement.UpdateOperationId, payload.UpdateOperationId, StringComparison.Ordinal))
            {
                return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "update-fence-mismatch");
            }

            var operation = await GrainFactory
                .GetGrain<IRunnerUpdateOperationGrain>(receipt.RunnerId)
                .GetAsync(payload.UpdateOperationId!);
            var fencedWork = operation?.AffectedWorks.SingleOrDefault(work =>
                string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal)
                && string.Equals(work.OwnerId, GrainKey, StringComparison.Ordinal)
                && string.Equals(work.WorkId, receipt.WorkId, StringComparison.Ordinal)
                && string.Equals(work.TaskRunId, receipt.TaskRunId, StringComparison.Ordinal));
            if (fencedWork is null
                || fencedWork.Status is not (RunnerUpdateWorkStatus.Marked or RunnerUpdateWorkStatus.Settled))
            {
                return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "update-fence-missing");
            }

            var recoveryGeneration = settlement.RecoveryGeneration + 1;
            var replacement = _workLifecycle.AllocateRecoveryAttempt(
                _run,
                task,
                recoveryGeneration,
                Now());

            // The replacement task, new work identity, and receipt ledger are
            // persisted together. Nothing below this commit can be observed
            // as an acknowledgement by the Runner.
            var replacementTask = _run.Stages
                .SelectMany(stage => stage.Tasks)
                .Single(task => string.Equals(task.Id, replacement.ReplacementTaskRunId, StringComparison.Ordinal));
            var recoveringTransition = replacementTask.AgentInterruption
                ?? throw new InvalidOperationException("Recovery replacement is missing interruption visibility.");
            _run.AppliedRecoveryReceipts.Add(new AppliedRuntimeRecoveryReceipt(
                receipt.ReceiptId,
                requestFingerprint,
                RuntimeRecoveryReceiptAckStatuses.Accepted,
                "replacement-created"));
            await CommitAsync([
                new AgentTaskInterruptionLifecycleChanged(
                    replacement.StageId,
                    replacement.ReplacementTaskRunId,
                    recoveringTransition)]);
            await ApplySessionInterruptionAsync(
                settlement.AgentSessionId,
                recoveringTransition);
            await SettleUpdateOperationWorkAsync(receipt, payload.UpdateOperationId!);
            _log.LogInformation(
                "run {run} recovered interrupted task {task}: generation={generation} work={work} turn={turn}",
                GrainKey,
                replacement.InterruptedTaskRunId,
                replacement.RecoveryGeneration,
                replacement.WorkId,
                replacement.AgentTurnId);
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.Accepted,
                "replacement-created");
        }

        var terminalResult = payload.NormalizedTerminalResult!;
        if (!string.Equals(payload.Fingerprint, RuntimeRecoveryReceiptFingerprint.For(terminalResult), StringComparison.OrdinalIgnoreCase))
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "result-fingerprint-mismatch");

        var item = _run.FindReportShape(receipt.TaskRunId, receipt.WorkId);
        if (item is null || !item.IsTask || _workflowItemTranslator is null)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "task-binding-mismatch");

        var translated = _workflowItemTranslator.TranslateResult(item, terminalResult, GrainKey);
        if (translated is not WorkflowItemTranslator.InboundReport.Task taskReport)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "terminal-result-invalid");

        var wasUpdateInterrupted = settlement.State == AgentResultSettlementState.RecoverablyInterrupted;
        var activeWork = wasUpdateInterrupted
            ? _run.FindRecoveryReceiptWork(receipt.TaskRunId, receipt.WorkId, receipt.RunnerId)
            : _run.FindReportableWork(receipt.TaskRunId, receipt.WorkId, receipt.RunnerId);
        if (activeWork is null || !activeWork.IsTask)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.Stale, "work-not-reportable");

        var racedUpdateInterruption = wasUpdateInterrupted
            ? task.AgentInterruption
            : null;
            : null;
        var effectiveReport = taskReport.Value with { TaskRunId = receipt.TaskRunId };
        try
        {
            RuntimeTaskFollowUps.Project(effectiveReport.AddTasks);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(
                "run {run} recovery receipt {receipt} rejected follow-up: {reason}",
                GrainKey, receipt.ReceiptId, ex.Message);
            effectiveReport = new TaskReport(
                activeWork.WorkId,
                TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: $"Recovery follow-up rejected: {ex.Message}");
        }

        effectiveReport = await BindTaskReportArtifactsAsync(activeWork, effectiveReport);
        var events = (await _workLifecycle.ApplyTaskReportAsync(
            _run,
            effectiveReport,
            activeWork.Item.Stage,
            receipt.TaskRunId)).ToList();

        if (racedUpdateInterruption is not null)
        {
            var recovered = racedUpdateInterruption with
            {
                State = AgentWorkInterruptionStates.Recovered,
                RecordedAt = Now(),
            };
            task.AgentInterruption = recovered;
            events.Add(new AgentTaskInterruptionLifecycleChanged(
                activeWork.Item.Stage,
                receipt.TaskRunId,
                recovered));
        }

        _run.AppliedRecoveryReceipts.Add(new AppliedRuntimeRecoveryReceipt(
            receipt.ReceiptId,
            requestFingerprint,
            RuntimeRecoveryReceiptAckStatuses.Accepted));
        await CommitAsync(events);
        if (wasUpdateInterrupted)
        {
            if (task.AgentInterruption is not null)
                await ApplySessionInterruptionAsync(settlement.AgentSessionId, task.AgentInterruption!);
            await SettleUpdateOperationWorkAsync(receipt, settlement.UpdateOperationId!);
        }
        else
        {
            await ReconcileAgentResultSettlementAsync();
            await GrainFactory
                .GetGrain<IRunnerUpdateOperationGrain>(receipt.RunnerId)
                .MarkReceiptAckedAsync(
                    WorkDispatchOwnerKinds.Workflow,
                    GrainKey,
                    receipt.WorkId,
                    receipt.TaskRunId);
        }
        return new RuntimeRecoveryReceiptAcknowledgement(
            receipt.ReceiptId,
            RuntimeRecoveryReceiptAckStatuses.Accepted);
    }

    public async Task<ReportAck> ReceiveTaskReportAsync(string workerId, string workId, TaskReport report)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        // A due runner-loss deadline is terminal for ordinary workflow work.
        // Reconcile it in the report turn so a late generation cannot win a
        // race against a reminder that has not executed yet.
        await ReconcileRunnerLossRecoveryAsync();

        if (!string.Equals(report.WorkId, workId, StringComparison.Ordinal)) return ReportAck.Stale;
        if (string.IsNullOrWhiteSpace(report.TaskRunId)) return ReportAck.Stale;
        var activeWork = _run.FindReportableWork(report.TaskRunId, workId, workerId);
        if (activeWork is null || !activeWork.IsTask || activeWork.TaskRunId is null)
            return ReportAck.Stale;

        var task = _run.Stages
            .Where(stage => string.Equals(stage.Id, activeWork.Item.Stage, StringComparison.Ordinal))
            .SelectMany(stage => stage.Tasks)
            .SingleOrDefault(candidate => string.Equals(candidate.Id, activeWork.TaskRunId, StringComparison.Ordinal));
        if (task is null) return ReportAck.Stale;
        var hadAgentResultSettlement = task.AgentResultSettlement is not null;
        var hadRunnerLossInterruption = task.Interruption is not null;
        var recoveringTransition = task.AgentInterruption is { State: AgentWorkInterruptionStates.Recovering }
            ? task.AgentInterruption
            : null;
        var recoveryOriginal = recoveringTransition is null
            ? null
            : _run.Stages
                .SelectMany(stage => stage.Tasks)
                .FirstOrDefault(candidate =>
                    candidate.Id != task.Id
                    && candidate.AgentInterruption is { } interruption
                    && string.Equals(interruption.UpdateOperationId, recoveringTransition.UpdateOperationId, StringComparison.Ordinal)
                    && interruption.RecoveryGeneration == recoveringTransition.RecoveryGeneration - 1
                    && string.Equals(interruption.State, AgentWorkInterruptionStates.Interrupted, StringComparison.Ordinal));
        var recoverySessionId = task.AgentResultSettlement?.AgentSessionId;

        _log.LogInformation("run {run} received task report for work {work}: {status} detail={detail}",
            GrainKey, activeWork.WorkId, report.Status, report.Detail ?? "(none)");

        TaskReport effectiveReport = report;
        if (report.Status == TaskReportStatus.Succeeded)
        {
            try
            {
                RuntimeTaskFollowUps.Project(report.AddTasks);
            }
            catch (InvalidOperationException ex)
            {
                _log.LogError(
                    "run {run} work {work} rejected recovery follow-up: {reason}",
                    GrainKey, activeWork.WorkId, ex.Message);
                effectiveReport = new TaskReport(
                    activeWork.WorkId,
                    TaskReportStatus.Failed,
                    Output: null,
                    Artifacts: null,
                    Detail: $"Recovery follow-up rejected: {ex.Message}");
            }
        }

        effectiveReport = await BindTaskReportArtifactsAsync(activeWork, effectiveReport);
        _run.ClearWorkInterruption(activeWork.WorkId, workerId);

        var events = (await _workLifecycle.ApplyTaskReportAsync(
            _run,
            effectiveReport,
            activeWork.Item.Stage,
            activeWork.TaskRunId)).ToList();
        if (recoveringTransition is not null)
        {
            var recovered = recoveringTransition with
            {
                State = AgentWorkInterruptionStates.Recovered,
                RecordedAt = Now(),
            };
            task.AgentInterruption = recovered;
            events.Add(new AgentTaskInterruptionLifecycleChanged(
                activeWork.Item.Stage,
                activeWork.TaskRunId,
                recovered));
        }

        await CommitAsync(events);
        if (recoveringTransition is not null)
        {
            await ApplySessionInterruptionAsync(recoverySessionId, task.AgentInterruption!);
            if (recoveryOriginal?.WorkId is { } originalWorkId
                && recoveryOriginal.AgentResultSettlement?.TaskRunId is { } originalTaskRunId)
            {
                await GrainFactory
                    .GetGrain<IRunnerUpdateOperationGrain>(recoveryOriginal.AgentResultSettlement!.RunnerId)
                    .MarkRecoverySettledAsync(
                        recoveringTransition.UpdateOperationId,
                        WorkDispatchOwnerKinds.Workflow,
                        GrainKey,
                        originalWorkId,
                        originalTaskRunId);
            }
        }
        if (hadAgentResultSettlement)
            await ReconcileAgentResultSettlementAsync();
        else
            await DeleteSnapshotBestEffortAsync(activeWork.WorkId);
        if (hadRunnerLossInterruption)
            await ReconcileRunnerLossRecoveryAsync(removeReminderWhenClear: true);
        return ReportAck.Accepted;
    }

    private async Task<TaskReport> BindTaskReportArtifactsAsync(
        WorkflowActiveWork activeWork,
        TaskReport report)
    {
        if (report.ArtifactUploadIds is not { Count: > 0 })
            return report;

        var variables = await _variableResolver.ResolveEffectiveVariableBundleAsync(
            GrainKey,
            activeWork.Item.Stage);
        var bindResult = await _artifactBindService.BindAsync(
            GrainKey,
            activeWork.WorkId,
            activeWork.TaskRunId!,
            report.ArtifactUploadIds.ToArray(),
            activeWork.Item.Artifacts,
            variables.Vars,
            GetProjectId(),
            GetIssueNumber());
        if (!bindResult.IsSuccess)
        {
            _log.LogWarning(
                "run {run} work {work} artifact binding failed: {reason}",
                GrainKey, activeWork.WorkId, bindResult.Error);
            return new TaskReport(
                activeWork.WorkId,
                TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: bindResult.Error ?? "artifact binding failed",
                Error: report.Error);
        }

        var boundArtifacts = bindResult.ArtifactRecordedEvents
            .Select(recorded => new ArtifactRef(recorded.Path))
            .ToList();
        return report with
        {
            Artifacts = boundArtifacts,
            ArtifactUploadIds = null,
        };
    }

    public async Task<ReportAck> ReceiveCheckReportAsync(string workerId, string workId, CheckReport report)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        await ReconcileRunnerLossRecoveryAsync();

        if (!_run.IsAssignedTo(workerId)) return ReportAck.Stale;
        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null || !activeWork.IsChecks)
            return ReportAck.Stale;

        _log.LogInformation("run {run} received check report for stage {Stage}: {Count} results",
            GrainKey, report.Stage, report.Results.Count);

        var hadRunnerLossInterruption = _run.CurrentStage().Interruption is not null;
        _run.ClearWorkInterruption(workId, workerId);
        var events = await _workLifecycle.ApplyCheckReportAsync(_run, report);
        _workLifecycle.RequeueRunningChecks(_run);

        await CommitAsync(events);
        if (hadRunnerLossInterruption)
            await ReconcileRunnerLossRecoveryAsync(removeReminderWhenClear: true);
        return ReportAck.Accepted;
    }

    private async Task ApplySessionInterruptionAsync(
        string? sessionId,
        AgentWorkInterruptionTransition transition)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var session = GrainFactory.GetGrain<IAgentSessionGrain>(sessionId);
        if (await session.GetAsync() is null) return;
        await session.ApplyInterruptionAsync(transition);
    }

    private async Task SettleUpdateOperationWorkAsync(
        RuntimeRecoveryReceipt receipt,
        string updateOperationId)
    {
        await GrainFactory
            .GetGrain<IRunnerUpdateOperationGrain>(receipt.RunnerId)
            .MarkWorkAsync(
                updateOperationId,
                WorkDispatchOwnerKinds.Workflow,
                GrainKey,
                receipt.WorkId,
                receipt.TaskRunId,
                RunnerUpdateWorkStatus.Settled);
    }

    private static bool MatchesReceiptBinding(
        AgentResultSettlement settlement,
        RuntimeRecoveryReceipt receipt) =>
        string.Equals(settlement.TaskRunId, receipt.TaskRunId, StringComparison.Ordinal)
        && string.Equals(settlement.WorkId, receipt.WorkId, StringComparison.Ordinal)
        && string.Equals(settlement.RunnerId, receipt.RunnerId, StringComparison.Ordinal)
        && string.Equals(settlement.AgentSessionId, receipt.AgentSessionId, StringComparison.Ordinal)
        && string.Equals(settlement.AgentTurnId, receipt.AgentTurnId, StringComparison.Ordinal)
        && string.Equals(settlement.Runtime, receipt.Runtime, StringComparison.Ordinal)
        && string.Equals(settlement.RuntimeSessionId, receipt.RuntimeSessionId, StringComparison.Ordinal);

    private async Task DeleteSnapshotBestEffortAsync(string workId)
    {
        try
        {
            await _dispatchSnapshotStore.DeleteAsync(GrainKey, workId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "run {run} failed to delete dispatch snapshot for work {work}; orphaned row will be swept at startup",
                GrainKey, workId);
        }
    }
}
