using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

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
        string updateOperationId)
    {
        RejectIfRunReloadRequired();
        if (_run is null)
            return ReportAck.Stale;

        var update = _run.MarkUpdateInterrupted(taskRunId, workId, runnerId, updateOperationId);
        if (update == AgentExecutionUpdate.Rejected)
            return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
        {
            var stage = _run.Stages.Single(stage => stage.Tasks.Any(task =>
                string.Equals(task.Id, taskRunId, StringComparison.Ordinal)));
            await CommitAsync([new AgentTaskUpdateInterrupted(
                stage.Id,
                taskRunId,
                workId,
                updateOperationId)]);
            await RemoveAgentResultSettlementReminderAsync();
        }

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
            return string.Equals(prior.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                ? new RuntimeRecoveryReceiptAcknowledgement(prior.ReceiptId, prior.Status, prior.Reason)
                : new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "receipt-id-reused");
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

            // T-004 owns replacement allocation. Retaining this receipt is
            // intentional: the Runner must replay it until arbitration exists.
            return new RuntimeRecoveryReceiptAcknowledgement(
                receipt.ReceiptId,
                RuntimeRecoveryReceiptAckStatuses.Retryable,
                "replacement-arbitration-pending");
        }

        if (settlement.State == AgentResultSettlementState.RecoverablyInterrupted)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.Stale, "execution-stopped");

        var terminalResult = payload.NormalizedTerminalResult!;
        if (!string.Equals(payload.Fingerprint, RuntimeRecoveryReceiptFingerprint.For(terminalResult), StringComparison.OrdinalIgnoreCase))
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "result-fingerprint-mismatch");

        var item = _run.FindReportShape(receipt.TaskRunId, receipt.WorkId);
        if (item is null || !item.IsTask || _workflowItemTranslator is null)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "task-binding-mismatch");

        var translated = _workflowItemTranslator.TranslateResult(item, terminalResult, GrainKey);
        if (translated is not WorkflowItemTranslator.InboundReport.Task taskReport)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, "terminal-result-invalid");

        var activeWork = _run.FindReportableWork(receipt.TaskRunId, receipt.WorkId, receipt.RunnerId);
        if (activeWork is null || !activeWork.IsTask)
            return new RuntimeRecoveryReceiptAcknowledgement(receipt.ReceiptId, RuntimeRecoveryReceiptAckStatuses.Stale, "work-not-reportable");

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
        var events = await _workLifecycle.ApplyTaskReportAsync(
            _run,
            effectiveReport,
            activeWork.Item.Stage,
            receipt.TaskRunId);

        _run.AppliedRecoveryReceipts.Add(new AppliedRuntimeRecoveryReceipt(
            receipt.ReceiptId,
            requestFingerprint,
            RuntimeRecoveryReceiptAckStatuses.Accepted));
        await CommitAsync(events);
        await ReconcileAgentResultSettlementAsync();
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

        var events = await _workLifecycle.ApplyTaskReportAsync(
            _run,
            effectiveReport,
            activeWork.Item.Stage,
            activeWork.TaskRunId);

        await CommitAsync(events);
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
