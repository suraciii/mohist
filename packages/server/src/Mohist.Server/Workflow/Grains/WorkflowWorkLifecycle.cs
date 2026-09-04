using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

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
        WorkflowRun run, TaskReport report, string stageId, string actionAttemptId)
    {
        var now = _owner.Now();
        var reportStage = run.Stages.SingleOrDefault(stage => stage.Id == stageId);
        var currentTask = reportStage?.Tasks.SingleOrDefault(task => task.Id == actionAttemptId);
        var events = new List<WorkflowEvent>();

        var taskAttempts = report.Status == TaskReportStatus.Succeeded
            ? RuntimeTaskFollowUps.Project(report.AddTasks)
            : [];

        if (report.Artifacts is { Count: > 0 })
        {
            foreach (var a in report.Artifacts)
            {
                events.Add(new WorkflowArtifactRecorded(_owner.GrainKey, actionAttemptId, a.Path, now));
            }
        }

        if (report.Status == TaskReportStatus.Succeeded)
        {
            if (currentTask is not null)
            {
                currentTask.TerminalResultFingerprint = report.TerminalResultFingerprint;
                currentTask.Output = report.Output;
                currentTask.Error = report.Error;
            }
            var hasFollowUpTasks = taskAttempts.Count > 0;
            var isFeedbackTask = currentTask?.CausedByFeedbackId is not null;
            var feedbackId = currentTask?.CausedByFeedbackId;
            events.AddRange(run.CompleteTask(
                stageId,
                actionAttemptId,
                now,
                advance: !hasFollowUpTasks && !isFeedbackTask));

            if (hasFollowUpTasks)
            {
                // Recovery is a generic Workflow concern. Every follow-up
                // chain is attributed to the failed source attempt so replay
                // fencing and source-authoritative self-retry apply to
                // ordinary tasks as well as historical verification tasks.
                var recoverySourceTaskId = currentTask is not null && IsRecoveryFailure(report)
                    ? currentTask.Id
                    : null;
                var followUpEvents = run.AddRuntimeTaskAttempts(
                    taskAttempts,
                    now,
                    recoverySourceTaskId,
                    feedbackId);
                events.AddRange(followUpEvents);
                _owner.Log.LogInformation(
                    "Workflow {Id} task {TaskId} produced {Count} follow-up tasks",
                    _owner.GrainKey, actionAttemptId, taskAttempts.Count);
            }

            ApprovalFeedback? resolved = null;
            if (feedbackId is not null && currentTask is not null)
            {
                resolved = run.ResolveFeedback(feedbackId, currentTask.Id, report.Output, now);
                if (resolved is not null)
                {
                    _owner.Log.LogInformation(
                        "Workflow {Id} resolved feedback {FeedbackId} via task {TaskId}",
                    _owner.GrainKey, feedbackId, currentTask.Id);
                }
            }

            if (resolved is not null)
                events.AddRange(run.Rerun(now));
            else if (feedbackId is not null)
                run.PrepareNextDispatchForOpenFeedback(now);
        }
        else
        {
            if (currentTask is not null)
            {
                currentTask.TerminalResultFingerprint = report.TerminalResultFingerprint;
                currentTask.Output = report.Output;
                currentTask.Error = report.Error;
            }
            var detail = report.Detail ?? (report.Output.HasValue ? report.Output.Value.GetRawText() : null);
            var taskResult = new TaskResult("failed", detail, report.Error);
            events.AddRange(run.FailTask(stageId, actionAttemptId, taskResult, now));
        }

        return events;
    }

    private static bool IsRecoveryFailure(TaskReport report)
    {
        if (report.Error is not null)
            return true;

        if (!report.Output.HasValue || report.Output.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;

        return report.Output.Value.TryGetProperty("promise", out var promise)
            && promise.ValueKind == System.Text.Json.JsonValueKind.String
            && string.Equals(promise.GetString(), "FAIL", StringComparison.Ordinal);
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

    public async Task<string?> MarkWorkflowActionAttemptRunningAsync(
        WorkflowRun run,
        string logicalTaskId,
        string workerId,
        string processGeneration,
        string? allocatedWorkId = null)
    {
        var current = run.CurrentStage();
        var currentTask = current.Tasks.FirstOrDefault(t => string.Equals(t.Id, logicalTaskId, StringComparison.Ordinal));
        if (currentTask?.Status == WorkflowActionAttemptStatus.Running)
        {
            _owner.CacheAssignedWorkerId(workerId);
            return currentTask.WorkId ?? logicalTaskId;
        }

        var workId = allocatedWorkId ?? logicalTaskId;
        var now = _owner.Now();
        var events = run.StartTask(workId, workerId, processGeneration, now);
        await _owner.SaveAsyncWithEvents(events);

        _owner.CacheAssignedWorkerId(workerId);
        return workId;
    }

    public string MarkChecksRunning(WorkflowRun run, string stage, string processGeneration, IReadOnlyList<CheckItem> items)
    {
        var checksWorkId = WorkflowRunExtensions.ChecksWorkIdFor(stage);
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = checksWorkId;
        currentStage.ChecksProcessGeneration = processGeneration;
        currentStage.TerminalChecksWorkId = null;
        currentStage.TerminalChecksWorkerId = null;
        currentStage.TerminalChecksResultFingerprint = null;
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
                var taskWorkId = run.CurrentStage().Tasks
                    .FirstOrDefault(task => string.Equals(task.Id, t.Id, StringComparison.Ordinal))?.WorkId
                    ?? t.Id;
                return WorkItem.Task(
                    stage: work.Stage,
                    id: taskWorkId,
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
        string processGeneration,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var currentStage = run.CurrentStage();

        var task = currentStage.Tasks.FirstOrDefault(t =>
            string.Equals(t.Id, workId, StringComparison.Ordinal)
            || (t.Status == WorkflowActionAttemptStatus.Pending
                && string.Equals(t.WorkId, workId, StringComparison.Ordinal)));
        if (task is not null)
        {
            if (task.Status == WorkflowActionAttemptStatus.Running)
            {
                _owner.CacheAssignedWorkerId(workerId);
                return task.WorkId ?? task.Id;
            }
            if (task.Status != WorkflowActionAttemptStatus.Pending) return null;

            var claimedWorkId = await MarkWorkflowActionAttemptRunningAsync(
                run,
                task.Id,
                workerId,
                processGeneration,
                task.WorkId ?? workId);
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

            var checksWorkId = MarkChecksRunning(run, currentStage.Id, processGeneration, items);
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
