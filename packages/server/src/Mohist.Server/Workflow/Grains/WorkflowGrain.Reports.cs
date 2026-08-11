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

    public async Task<ReportAck> ObserveAgentExecutionAsync(AgentExecutionObservation observation)
    {
        RejectIfRunReloadRequired();
        if (_run is null) return ReportAck.Stale;

        var update = _run.ObserveAgentExecution(observation, Now());
        if (update == AgentExecutionUpdate.Rejected) return ReportAck.Stale;
        if (update == AgentExecutionUpdate.Updated)
            await CommitAsync([]);
        return ReportAck.Accepted;
    }

    public async Task<ReportAck> FailActiveWorkAsync(string workerId, string message)
    {
        RejectIfRunReloadRequired();
        if (_run is null || !_run.IsAssignedTo(workerId)) return ReportAck.Stale;
        var activeWork = _run.CurrentActiveWorkFor(workerId);
        if (activeWork is null) return ReportAck.Stale;

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
        if (_run is null || !_run.IsAssignedTo(workerId)) return ReportAck.Stale;
        var activeWork = _run.FindActiveWork(workId, workerId);
        if (activeWork is null || !activeWork.IsTask || activeWork.TaskRunId is null)
            return ReportAck.Stale;

        _log.LogInformation("run {run} received task report for work {work}: {status} detail={detail}",
            GrainKey, workId, report.Status, report.Detail ?? "(none)");

        var events = await _workLifecycle.ApplyTaskReportAsync(_run, report, activeWork.TaskRunId);

        await CommitAsync(events);
        await DeleteSnapshotBestEffortAsync(workId);
        return ReportAck.Accepted;
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
