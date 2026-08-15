using Mohist.Server.Workflow.Domain.Run;

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

    public Task<bool> CanStartAgentCleanupAsync(AgentExecutionBinding binding)
    {
        RejectIfRunReloadRequired();
        return Task.FromResult(_run?.CanStartAgentCleanup(binding) == true);
    }

    public async Task<ReportAck> ObserveAgentExecutionAsync(AgentExecutionObservation observation)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

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

    public async Task<ReportAck> ReceiveTaskReportAsync(string workerId, string workId, TaskReport report)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;
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
        if (_run is null || !_run.IsAssignedTo(workerId)) return ReportAck.Stale;
        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null || !activeWork.IsChecks)
            return ReportAck.Stale;

        _log.LogInformation("run {run} received check report for stage {Stage}: {Count} results",
            GrainKey, report.Stage, report.Results.Count);

        var events = await _workLifecycle.ApplyCheckReportAsync(_run, report);
        _workLifecycle.RequeueRunningChecks(_run);

        await CommitAsync(events);
        return ReportAck.Accepted;
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
