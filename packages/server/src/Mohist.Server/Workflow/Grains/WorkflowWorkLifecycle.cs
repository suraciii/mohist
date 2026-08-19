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
        WorkflowRun run, TaskReport report, string stageId, string taskRunId)
    {
        var now = _owner.Now();
        var reportStage = run.Stages.SingleOrDefault(stage => stage.Id == stageId);
        var currentTask = reportStage?.Tasks.SingleOrDefault(task => task.Id == taskRunId);
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

        // Classify the report at the verification boundary BEFORE the normal
        // task transition so the lane outcome (pass/fail/timeout) is visible
        // to the stage gate in the same state commit that advances the stage.
        // The final lane is usually the last build task: if the outcome were
        // applied after CompleteTask/Advance, the gate would evaluate while
        // that lane is still pending and a fully passed run would never
        // advance past the build stage.
        ApplyLaneOutcome(currentTask, report, now);

        if (report.Status == TaskReportStatus.Succeeded)
        {
            if (currentTask is not null)
            {
                currentTask.Output = report.Output;
                currentTask.Error = report.Error;
            }
            var hasFollowUpTasks = taskAttempts.Count > 0;
            events.AddRange(run.CompleteTask(stageId, taskRunId, now, advance: !hasFollowUpTasks));

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
                var recoverySourceTaskId = currentTask?.Lane is not null
                    ? currentTask.Id
                    : null;
                var followUpEvents = run.AddRuntimeTaskAttempts(
                    taskAttempts,
                    now,
                    recoverySourceTaskId);
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
            events.AddRange(run.FailTask(stageId, taskRunId, taskResult, now));
        }

        // ApplyLaneOutcome already ran before the task transition; it must not
        // run again here (the lane metadata is write-once per attempt).

        return events;
    }

    /// <summary>
    /// Persists the additive verification-lane outcome for a recognized lane
    /// attempt on the same commit as the normal task transition. A
    /// <c>recover:fix-ci</c> helper is not a lane, so its report leaves the
    /// lane metadata untouched (it can never promote a lane to <c>pass</c>).
    /// The lane carries its stable identity, order, configured budget,
    /// attempt identity (<see cref="TaskRun.Id"/> / <see cref="TaskRun.WorkId"/>),
    /// and the failure or timeout diagnostics when applicable.
    /// </summary>
    private static void ApplyLaneOutcome(TaskRun? task, TaskReport report, DateTimeOffset now)
    {
        if (task?.Lane is null) return;

        var outcome = VerificationLaneClassifier.Classify(task.DefinitionId, report);
        if (outcome is null) return;

        var detail = report.Detail ?? (report.Output.HasValue ? report.Output.Value.GetRawText() : null);
        task.Lane = task.Lane with
        {
            Outcome = outcome.Value,
            WorkId = task.WorkId ?? task.Lane.WorkId,
            Error = outcome.Value == VerificationLaneOutcome.Pass
                ? null
                : report.Error ?? task.Lane.Error,
            // Pass evidence needs no diagnostic; fail/timeout keep the exact
            // detail text from the report or its output payload.
            Detail = outcome.Value == VerificationLaneOutcome.Pass ? null : detail,
            FinishedAt = now,
        };
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
        string workerId,
        string? allocatedWorkId = null)
    {
        var current = run.CurrentStage();
        var currentTask = current.Tasks.FirstOrDefault(t => string.Equals(t.Id, logicalTaskId, StringComparison.Ordinal));
        if (currentTask?.Status == TaskRunStatus.Running)
        {
            _owner.CacheAssignedWorkerId(workerId);
            return currentTask.WorkId ?? logicalTaskId;
        }

        var workId = allocatedWorkId ?? logicalTaskId;
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
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var currentStage = run.CurrentStage();

        var task = currentStage.Tasks.FirstOrDefault(t =>
            string.Equals(t.Id, workId, StringComparison.Ordinal)
            || (t.Status == TaskRunStatus.Pending
                && string.Equals(t.WorkId, workId, StringComparison.Ordinal)));
        if (task is not null)
        {
            if (task.Status == TaskRunStatus.Running)
            {
                _owner.CacheAssignedWorkerId(workerId);
                return task.WorkId ?? task.Id;
            }
            if (task.Status != TaskRunStatus.Pending) return null;

            var claimedWorkId = await MarkTaskRunningAsync(
                run,
                task.Id,
                workerId,
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

            var checksWorkId = MarkChecksRunning(run, currentStage.Id, items);
            // Checks claims emit no events, but the Running state must persist.
            await commitAsync([]);
            return checksWorkId;
        }

        return null;
    }

    /// <summary>
    /// Converts a fenced Agent attempt into one new pending attempt. The old
    /// TaskRun remains in the stage as immutable interruption history; the
    /// returned work id is already persisted on the replacement so a poll can
    /// offer exactly that identity after the commit.
    /// </summary>
    public WorkflowRecoveryAttempt AllocateRecoveryAttempt(
        WorkflowRun run,
        TaskRun interrupted,
        int recoveryGeneration,
        DateTimeOffset now)
    {
        var stage = run.Stages.Single(candidate => candidate.Tasks.Contains(interrupted));
        var workId = AllocateRecoveryWorkId(run, interrupted.WorkId!, recoveryGeneration);
        var turnId = $"recovery-turn:{workId}";
        var replacement = TaskRun.MakeRecoveryAttempt(
            interrupted,
            stage.Tasks,
            stage.Attempt,
            recoveryGeneration,
            workId,
            turnId,
            run.Stages.SelectMany(candidate => candidate.Tasks),
            now);

        interrupted.Status = TaskRunStatus.Interrupted;
        interrupted.FinishedAt = now;
        var index = stage.Tasks.IndexOf(interrupted);
        stage.Tasks.Insert(index + 1, replacement);
        stage.Status = StageRunStatus.Running;
        run.Status = WorkflowRunStatus.Ready;
        run.ReadySince = now;
        return new WorkflowRecoveryAttempt(stage.Id, interrupted.Id, replacement.Id, workId, turnId, recoveryGeneration);
    }

    private static string AllocateRecoveryWorkId(WorkflowRun run, string originalWorkId, int recoveryGeneration)
    {
        var candidate = $"{originalWorkId}.recovery.{recoveryGeneration}";
        var occupied = run.Stages
            .SelectMany(stage => stage.Tasks)
            .SelectMany(task => new[] { task.Id, task.WorkId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        if (occupied.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var disambiguated = $"{candidate}.run{suffix}";
            if (occupied.Add(disambiguated))
                return disambiguated;
        }
    }

    public void RequeueRunningChecks(WorkflowRun run)
    {
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = null;
        currentStage.Interruption = null;
        foreach (var ch in currentStage.Checks.Where(c => c.Status == StageCheckStatus.Running))
        {
            ch.Status = StageCheckStatus.Pending;
            ch.StartedAt = null;
        }
    }
}

internal sealed record WorkflowRecoveryAttempt(
    string StageId,
    string InterruptedTaskRunId,
    string ReplacementTaskRunId,
    string WorkId,
    string AgentTurnId,
    int RecoveryGeneration);
