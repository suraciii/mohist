using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    public async Task<WorkReportVerdict> BindAgentExecutionAsync(AgentExecutionBinding binding)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;

        var update = _run.BindAgentExecution(binding);
        if (update == AgentExecutionUpdate.Rejected) return WorkReportVerdict.Refused;
        if (update == AgentExecutionUpdate.Updated)
            await CommitAsync([]);
        return WorkReportVerdict.Accepted;
    }

     public Task<bool> CanStartAgentCleanupAsync(AgentExecutionBinding binding)
    {
        RejectIfRunReloadRequired();
        return Task.FromResult(_run?.CanStartAgentCleanup(binding) == true);
    }

    public async Task<WorkReportVerdict> ObserveAgentExecutionAsync(AgentExecutionObservation observation)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;

        // Reconcile a due settlement before applying a late observation. The
        // deadline owns the race, so an observation cannot update a result
        // after the server has already decided it is blocked.
        await ReconcileAgentResultSettlementIfDueAsync();

        var existing = _run.FindAgentResultSettlementTask(observation.Binding);
        var wasAwaitingResult = existing?.Task.AgentResultSettlement?.State == AgentResultSettlementState.AwaitingResult;
        var update = _run.ObserveAgentExecution(observation, Now(), _agentResultSettlementTimeout);
        if (update == AgentExecutionUpdate.Rejected) return WorkReportVerdict.Refused;
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
        return WorkReportVerdict.Accepted;
    }

    public async Task<WorkReportVerdict> ObserveAgentResultUnknownAsync(
        string workerId,
        string taskRunId,
        string workId,
        string reasonCode,
        string? message = null)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;

        await ReconcileAgentResultSettlementIfDueAsync();

        var attempt = _run.FindReportableTaskAttempt(taskRunId, workId, workerId);
        var wasAwaitingResult = attempt?.SettlementState == AgentResultSettlementState.AwaitingResult;
        var observedAt = Now();
        var update = attempt is not null
            ? _run.ObserveAgentResultUnknown(taskRunId, workId, workerId, reasonCode, message, observedAt, _agentResultSettlementTimeout)
            : AgentExecutionUpdate.Rejected;
        if (update == AgentExecutionUpdate.Rejected) return WorkReportVerdict.Refused;
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
        return WorkReportVerdict.Accepted;
    }

    public async Task<WorkReportVerdict> ObserveAgentRunnerDisconnectedAsync(string workerId)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;

        await ReconcileAgentResultSettlementIfDueAsync();

        var active = _run.CurrentActiveWorkFor(workerId);
        var taskRunId = active?.TaskRunId;
        var task = taskRunId is null
            ? null
            : _run.Stages.SelectMany(stage => stage.Tasks).SingleOrDefault(candidate => candidate.Id == taskRunId);
        var wasAwaitingResult = task?.AgentResultSettlement?.State == AgentResultSettlementState.AwaitingResult;
        var update = _run.ObserveAgentRunnerDisconnected(workerId, Now(), _agentResultSettlementTimeout);
        if (update == AgentExecutionUpdate.Rejected) return WorkReportVerdict.Refused;
        if (update == AgentExecutionUpdate.Updated)
        {
            var deadline = task?.AgentResultSettlement?.DeadlineAt;
            await CommitAsync(wasAwaitingResult && active is not null && task is not null && deadline is { } due
                ? [new AgentTaskResultUnconfirmed(active.Item.Stage, task.Id, active.WorkId, "runner-disconnected", due)]
                : []);
        }

        await ReconcileAgentResultSettlementAsync();
        return WorkReportVerdict.Accepted;
    }

    public async Task<WorkReportVerdict> AbandonActiveWorkAsync(string workerId, string workId, string reason)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return WorkReportVerdict.Refused;

        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null) return WorkReportVerdict.Refused;
        if (activeWork.IsTask && _run.CurrentStage().RunningTask?.AgentResultSettlement is not null)
            return WorkReportVerdict.Refused;

        await _stageLockCoordinator.ReleaseCurrentStageLocksAsync(reason);

        IReadOnlyList<WorkflowEvent> events;
        if (_run.Status == WorkflowRunStatus.Paused)
        {
            if (activeWork.IsTask)
            {
                if (!_run.RequeueTaskAfterPausedStop(workId, workerId))
                    return WorkReportVerdict.Refused;
            }
            else if (activeWork.IsChecks)
            {
                _workLifecycle.RequeueRunningChecks(_run);
            }
            else
            {
                return WorkReportVerdict.Refused;
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
                return WorkReportVerdict.Refused;
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
            return WorkReportVerdict.Refused;
        }

        await CommitAsync(events);
        if (activeWork.IsTask)
            await DeleteSnapshotBestEffortAsync(workId);
        return WorkReportVerdict.Accepted;
    }

    public async Task<WorkReportVerdict> RejectActiveWorkDispatchAsync(string workerId, string workId, ExecutionError error)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return WorkReportVerdict.Refused;
        var activeWork = _run.FindReportableWork(workId, workerId);
        if (activeWork is null || !activeWork.IsTask) return WorkReportVerdict.Refused;

        var task = _run.CurrentStage().RunningTask;
        if (task is not null) task.Error = error;

        var events = _run.FailTask(new TaskResult("failed", error.Message, error), Now());
        if (events.Count == 0) return WorkReportVerdict.Refused;

        _log.LogWarning(
            "run {run} rejected dispatch for work {work}: {code} {reason}",
            GrainKey, workId, error.Code, error.Message);
        await CommitAsync(events);
        await DeleteSnapshotBestEffortAsync(workId);
        return WorkReportVerdict.Accepted;
    }

     public async Task<WorkReportVerdict> ReceiveTaskReportAsync(
        string workerId,
        string workId,
        TaskReport report,
        AgentExecutionBinding? agentBinding = null)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;

        // A due runner-loss deadline is terminal for ordinary workflow work.
        // Reconcile it in the report turn so a late generation cannot win a
        // race against a reminder that has not executed yet.
        await ReconcileRunnerLossRecoveryAsync();

        if (!string.Equals(report.WorkId, workId, StringComparison.Ordinal)) return WorkReportVerdict.Refused;
        if (string.IsNullOrWhiteSpace(report.TaskRunId)) return WorkReportVerdict.Refused;
        if (agentBinding is not null)
        {
            if (!string.Equals(agentBinding.RunnerId, workerId, StringComparison.Ordinal)
                || !string.Equals(agentBinding.WorkId, workId, StringComparison.Ordinal)
                || !string.Equals(agentBinding.TaskRunId, report.TaskRunId, StringComparison.Ordinal))
            {
                return WorkReportVerdict.Refused;
            }

            // A Workflow Agent result must prove the complete execution
            // identity, including the binding persisted by the same turn.
            await ReconcileAgentResultSettlementIfDueAsync();
            var bound = _run.FindBoundAgentExecution(agentBinding.TaskRunId, workId, workerId)
                ?? _run.FindTerminalBoundAgentExecution(agentBinding.TaskRunId, workId, workerId);
            if (bound is null || !MatchesExecutionBinding(bound, agentBinding))
                return WorkReportVerdict.Refused;
        }

        var activeWork = _run.FindReportableWork(report.TaskRunId, workId, workerId);
        if (activeWork is null || !activeWork.IsTask || activeWork.TaskRunId is null)
        {
            var terminalWork = _run.FindTerminalReportAttempt(report.TaskRunId, workId, workerId);
            if (terminalWork?.TaskRunId is null)
                return WorkReportVerdict.Refused;

            var terminalTask = _run.Stages
                .Where(stage => string.Equals(stage.Id, terminalWork.Item.Stage, StringComparison.Ordinal))
                .SelectMany(stage => stage.Tasks)
                .Single(candidate => string.Equals(candidate.Id, terminalWork.TaskRunId, StringComparison.Ordinal));
            return terminalTask.TerminalResultFingerprint is not null
                && report.TerminalResultFingerprint is not null
                && string.Equals(terminalTask.TerminalResultFingerprint, report.TerminalResultFingerprint, StringComparison.Ordinal)
                && (agentBinding is null || terminalTask.TerminalExecutionBinding is not null)
                ? WorkReportVerdict.Accepted
                : WorkReportVerdict.Refused;
        }

        var task = _run.Stages
            .Where(stage => string.Equals(stage.Id, activeWork.Item.Stage, StringComparison.Ordinal))
            .SelectMany(stage => stage.Tasks)
            .SingleOrDefault(candidate => string.Equals(candidate.Id, activeWork.TaskRunId, StringComparison.Ordinal));
        if (task is null) return WorkReportVerdict.Refused;
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

        var artifactUploadIds = effectiveReport.ArtifactUploadIds?.ToArray();
        effectiveReport = await ValidateTaskReportArtifactsAsync(activeWork, effectiveReport);
        _run.ClearWorkInterruption(activeWork.WorkId, workerId);

        var events = (await _workLifecycle.ApplyTaskReportAsync(
            _run,
            effectiveReport,
            activeWork.Item.Stage,
            activeWork.TaskRunId)).ToList();
        if (artifactUploadIds is { Length: > 0 } && effectiveReport.Artifacts is { Count: > 0 })
        {
            await CommitWithArtifactsAsync(events, new WorkflowArtifactBindingIntent(
                activeWork.WorkId,
                activeWork.TaskRunId,
                artifactUploadIds,
                Now(),
                GetProjectId(),
                GetIssueNumber()));
        }
        else
        {
            _reportPersistenceWorkId = activeWork.WorkId;
            try
            {
                await CommitAsync(events);
            }
            finally
            {
                _reportPersistenceWorkId = null;
            }
        }
        if (hadAgentResultSettlement)
            await ReconcileAgentResultSettlementAsync();
        else
            await DeleteSnapshotBestEffortAsync(activeWork.WorkId);
        if (hadRunnerLossInterruption)
            await ReconcileRunnerLossRecoveryAsync(removeReminderWhenClear: true);
        return WorkReportVerdict.Accepted;
    }

    private async Task<TaskReport> ValidateTaskReportArtifactsAsync(
        WorkflowActiveWork activeWork,
        TaskReport report)
    {
        if (report.ArtifactUploadIds is not { Count: > 0 })
            return report;

        var variables = await _variableResolver.ResolveEffectiveVariableBundleAsync(
            GrainKey,
            activeWork.Item.Stage);
        var bindResult = await _artifactBindService.ValidateAsync(
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
            return report with
            {
                Status = TaskReportStatus.Failed,
                Output = null,
                Artifacts = null,
                Detail = bindResult.Error ?? "artifact binding failed",
                Error = report.Error,
            };
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

    public async Task<WorkReportVerdict> ReceiveCheckReportAsync(string workerId, string workId, CheckReport report)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;

        await ReconcileRunnerLossRecoveryAsync();

        var terminalStage = _run.Stages.SingleOrDefault(stage =>
            string.Equals(stage.TerminalChecksWorkId, workId, StringComparison.Ordinal));
        if (terminalStage is not null)
        {
            return string.Equals(terminalStage.TerminalChecksWorkerId, workerId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(report.TerminalResultFingerprint)
                && string.Equals(
                    terminalStage.TerminalChecksResultFingerprint,
                    report.TerminalResultFingerprint,
                    StringComparison.Ordinal)
                ? WorkReportVerdict.Accepted
                : WorkReportVerdict.Refused;
        }

        if (!_run.IsAssignedTo(workerId)) return WorkReportVerdict.Refused;
        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null || !activeWork.IsChecks)
            return WorkReportVerdict.Refused;

        _log.LogInformation("run {run} received check report for stage {Stage}: {Count} results",
            GrainKey, report.Stage, report.Results.Count);

        var currentStage = _run.CurrentStage();
        var hadRunnerLossInterruption = currentStage.Interruption is not null;
        currentStage.TerminalChecksWorkId = workId;
        currentStage.TerminalChecksWorkerId = workerId;
        currentStage.TerminalChecksResultFingerprint = report.TerminalResultFingerprint;
        _run.ClearWorkInterruption(workId, workerId);
        var events = await _workLifecycle.ApplyCheckReportAsync(_run, report);
        _workLifecycle.RequeueRunningChecks(_run);

        await CommitAsync(events);
        if (hadRunnerLossInterruption)
            await ReconcileRunnerLossRecoveryAsync(removeReminderWhenClear: true);
        return WorkReportVerdict.Accepted;
    }

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
