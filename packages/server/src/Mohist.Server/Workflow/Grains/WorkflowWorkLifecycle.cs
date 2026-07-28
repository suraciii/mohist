using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

internal sealed class WorkflowWorkLifecycle
{
    private readonly IWorkflowGrainContext _owner;

    public WorkflowWorkLifecycle(IWorkflowGrainContext owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Artifact events precede task completion so history records the produced
    /// artifact before the producing task closes.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowEvent>> ApplyTaskReportAsync(
        WorkflowRun run, TaskReport report, string taskRunId)
    {
        var now = _owner.Now();
        var currentStage = run.CurrentStage();
        var currentTask = currentStage?.Tasks.FirstOrDefault(t => t.Id == taskRunId);
        var events = new List<WorkflowEvent>();

        var taskAttempts = report.Status == TaskReportStatus.Succeeded
            ? RuntimeTaskFollowUps.Project(report.AddTasks)
            : [];

        if (report.Artifacts is { Count: > 0 })
        {
            foreach (var a in report.Artifacts)
            {
                events.Add(new WorkflowArtifactRecorded(_owner.GrainKey, taskRunId, a.Path, now));
            }
        }

        if (report.Status == TaskReportStatus.Succeeded)
        {
            if (currentTask is not null)
            {
                currentTask.Output = report.Output;
                currentTask.Error = report.Error;
            }
            var hasFollowUpTasks = taskAttempts.Count > 0;
            events.AddRange(run.CompleteTask(now, advance: !hasFollowUpTasks));

            if (currentTask?.CausedByFeedbackId is { } feedbackId)
            {
                var resolved = run.ResolveFeedback(feedbackId, currentTask.Id, report.Output, now);
                if (resolved is not null)
                {
                    _owner.Log.LogInformation(
                        "Workflow {Id} resolved feedback {FeedbackId} via task {TaskId}",
                        _owner.GrainKey, feedbackId, currentTask.Id);
                }
            }

            if (hasFollowUpTasks)
            {
                var followUpEvents = run.AddRuntimeTaskAttempts(taskAttempts, now);
                events.AddRange(followUpEvents);
                _owner.Log.LogInformation(
                    "Workflow {Id} task {TaskId} produced {Count} follow-up tasks",
                    _owner.GrainKey, taskRunId, taskAttempts.Count);
            }
        }
        else
        {
            if (currentTask is not null)
            {
                currentTask.Output = report.Output;
                currentTask.Error = report.Error;
            }
            var detail = report.Detail ?? (report.Output.HasValue ? report.Output.Value.GetRawText() : null);
            var taskResult = new TaskResult("failed", detail, report.Error);
            events.AddRange(run.FailTask(taskResult, now));
        }

        return events;
    }

    public Task<IReadOnlyList<WorkflowEvent>> ApplyCheckReportAsync(WorkflowRun run, CheckReport report)
    {
        var now = _owner.Now();
        return Task.FromResult<IReadOnlyList<WorkflowEvent>>(run.ProcessCheckResults(report.Results, now));
    }

    public async Task<IReadOnlyList<WorkflowEvent>> AbandonRunningWorkAsync(WorkflowRun run, string reason)
    {
        await _owner.ReleaseCurrentStageLocks(reason);

        var currentStage = run.CurrentStage();
        if (currentStage is not null)
        {
            RequeueRunningChecks(run);
        }

        var runningTask = run.CurrentStage().RunningTask;
        if (runningTask is not null)
        {
            var events = run.FailTaskForStopped(reason, _owner.Now());
            return events;
        }

        return [];
    }

    public async Task<string?> MarkTaskRunningAsync(
        WorkflowRun run,
        string logicalTaskId,
        string workerId)
    {
        var current = run.CurrentStage();
        var currentTask = current.Tasks.FirstOrDefault(t => t.Id == logicalTaskId);
        if (currentTask?.Status == TaskRunStatus.Running)
        {
            _owner.CacheAssignedWorkerId(workerId);
            return currentTask.WorkId ?? logicalTaskId;
        }

        var workId = logicalTaskId;
        var now = _owner.Now();
        var events = run.StartTask(workId, workerId, now);
        await _owner.SaveAsyncWithEvents(events);

        _owner.CacheAssignedWorkerId(workerId);
        return workId;
    }

    public string MarkChecksRunning(WorkflowRun run, string stage, IReadOnlyList<CheckItem> items)
    {
        var checksWorkId = WorkflowRunExtensions.ChecksWorkIdFor(stage);
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = checksWorkId;
        var now = _owner.Now();
        foreach (var item in items)
        {
            var check = currentStage.Checks.FirstOrDefault(c => c.Name == item.Name);
            if (check is not null)
            {
                check.Status = StageCheckStatus.Running;
                check.StartedAt = now;
            }
        }
        run.Status = WorkflowRunStatus.Running;
        return checksWorkId;
    }

    public WorkItem? BuildClaimableWorkItem(WorkflowRun run, WorkflowWork work)
    {
        switch (work)
        {
            case WorkflowTaskWork t:
            {
                return WorkItem.Task(
                    stage: work.Stage,
                    id: t.Id,
                    title: t.Title,
                    uses: t.Uses,
                    with: t.With,
                    artifacts: t.Artifacts,
                    setVars: t.SetVars,
                    recovery: t.Recovery,
                    recoveryRemaining: t.RecoveryRemaining,
                    expect: t.Expect);
            }
            case WorkflowChecksWork ch:
            {
                return WorkItem.Checks(work.Stage, WorkflowRunExtensions.ChecksWorkIdFor(work.Stage), ch.Items);
            }
            default:
                return null;
        }
    }

    public async Task<string?> ClaimWorkAsync(
        WorkflowRun run,
        string workId,
        string workerId,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var currentStage = run.CurrentStage();

        var task = currentStage.Tasks.FirstOrDefault(t => t.Id == workId);
        if (task is not null)
        {
            if (task.Status == TaskRunStatus.Running)
            {
                _owner.CacheAssignedWorkerId(workerId);
                return task.WorkId ?? task.Id;
            }
            if (task.Status != TaskRunStatus.Pending) return null;

            var claimedWorkId = await MarkTaskRunningAsync(run, task.Id, workerId);
            return claimedWorkId;
        }

        if (workId == WorkflowRunExtensions.ChecksWorkIdFor(currentStage.Id))
        {
            if (!string.IsNullOrWhiteSpace(currentStage.ChecksWorkId))
            {
                _owner.CacheAssignedWorkerId(workerId);
                return currentStage.ChecksWorkId;
            }

            var items = currentStage.Checks
                .Where(c => c.Status == StageCheckStatus.Pending)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();
            if (items.Count == 0) return null;

            var checksWorkId = MarkChecksRunning(run, currentStage.Id, items);
            // Checks claims emit no events, but the Running state must persist.
            await commitAsync([]);
            return checksWorkId;
        }

        return null;
    }

    public void RequeueRunningChecks(WorkflowRun run)
    {
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = null;
        foreach (var ch in currentStage.Checks.Where(c => c.Status == StageCheckStatus.Running))
        {
            ch.Status = StageCheckStatus.Pending;
            ch.StartedAt = null;
        }
    }
}
