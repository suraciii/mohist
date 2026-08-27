using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{

    public async Task<WorkReportVerdict> AbandonActiveWorkAsync(string workerId, string workId, string reason)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return WorkReportVerdict.Refused;

        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null) return WorkReportVerdict.Refused;
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
        TaskReport report)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return WorkReportVerdict.Refused;
        await ReconcileRunnerLossRecoveryAsync();
        if (!string.Equals(report.WorkId, workId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(report.ActionAttemptId))
            return WorkReportVerdict.Refused;

        var activeWork = _run.FindReportableWork(report.ActionAttemptId, workId, workerId);
        if (activeWork is null || !activeWork.IsTask || activeWork.ActionAttemptId is null)
        {
            var terminalWork = _run.FindTerminalReportAttempt(report.ActionAttemptId, workId, workerId);
            if (terminalWork?.ActionAttemptId is null) return WorkReportVerdict.Refused;
            var terminalTask = _run.Stages
                .SelectMany(stage => stage.Tasks)
                .Single(task => string.Equals(task.Id, terminalWork.ActionAttemptId, StringComparison.Ordinal));
            return terminalTask.TerminalResultFingerprint is not null
                && report.TerminalResultFingerprint is not null
                && string.Equals(terminalTask.TerminalResultFingerprint, report.TerminalResultFingerprint, StringComparison.Ordinal)
                ? WorkReportVerdict.Accepted
                : WorkReportVerdict.Refused;
        }

        var hadRunnerLossInterruption = _run.Stages.SelectMany(stage => stage.Tasks)
            .Single(task => string.Equals(task.Id, activeWork.ActionAttemptId, StringComparison.Ordinal))
            .Interruption is not null;
        var effectiveReport = report;
        if (report.Status == TaskReportStatus.Succeeded)
        {
            try { RuntimeTaskFollowUps.Project(report.AddTasks); }
            catch (InvalidOperationException ex)
            {
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
        var events = await _workLifecycle.ApplyTaskReportAsync(
            _run, effectiveReport, activeWork.Item.Stage, activeWork.ActionAttemptId);
        if (artifactUploadIds is { Length: > 0 } && effectiveReport.Artifacts is { Count: > 0 })
        {
            await CommitWithArtifactsAsync(events, new WorkflowArtifactBindingIntent(
                activeWork.WorkId, activeWork.ActionAttemptId, artifactUploadIds, Now(), GetProjectId(), GetIssueNumber()));
        }
        else
        {
            _reportPersistenceWorkId = activeWork.WorkId;
            try { await CommitAsync(events); }
            finally { _reportPersistenceWorkId = null; }
        }
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
            activeWork.ActionAttemptId!,
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
