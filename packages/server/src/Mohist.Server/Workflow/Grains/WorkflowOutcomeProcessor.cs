using System.Text.Json;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Handles workflow outcome transitions inside the grain. The grain remains
/// the commit and event-dispatch boundary; this helper mutates the supplied
/// run and returns or commits events through grain-owned callbacks.
/// </summary>
internal sealed class WorkflowOutcomeProcessor
{
    private readonly IWorkflowGrainContext _owner;

    public WorkflowOutcomeProcessor(IWorkflowGrainContext owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Applies a runner task outcome. Artifact events precede task completion
    /// so history records the produced artifact before the producing task closes.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowEvent>> ProcessTaskOutcomeAsync(
        WorkflowRun run, TaskOutcome outcome, string taskRunId, string workId)
    {
        var currentStage = run.CurrentStage();
        var currentTask = currentStage?.Tasks.FirstOrDefault(t => t.Id == taskRunId);
        var events = new List<WorkflowEvent>();

        if (outcome.Artifacts is { Count: > 0 })
        {
            foreach (var a in outcome.Artifacts)
            {
                events.Add(new WorkflowArtifactRecorded(_owner.GrainKey, taskRunId, a.Path, DateTimeOffset.UtcNow));
            }
        }

        if (outcome.Status == OutcomeStatus.Passed)
        {
            if (currentTask is not null)
                currentTask.Output = ParseOutputToJsonElement(outcome.Output);
            if (currentTask?.CausedByFeedbackId is { } feedbackId)
            {
                var resolved = run.ResolveFeedback(feedbackId, currentTask.Id, outcome.Output);
                if (resolved is not null)
                {
                    _owner.Log.LogInformation(
                        "Workflow {Id} resolved feedback {FeedbackId} via task {TaskId}",
                        _owner.GrainKey, feedbackId, currentTask.Id);
                }
            }
            events.AddRange(run.CompleteTask());

            if (outcome.AddTasks is { Count: > 0 } addTasks)
            {
                var current = run.CurrentStage();
                var taskDefs = addTasks.Select(t =>
                {
                    var with = WorkflowDispatchHelpers.ParseWith(t.With);
                    return new TaskDefinition(t.Id, t.Title, t.Uses, with, Recovery: t.Recovery);
                }).ToList();
                var recoveryEvents = run.AddRuntimeTasks(taskDefs);
                events.AddRange(recoveryEvents);
                _owner.Log.LogInformation(
                    "Workflow {Id} task {TaskId} produced {Count} recovery tasks",
                    _owner.GrainKey, taskRunId, addTasks.Count);
            }
        }
        else
        {
            if (currentTask is not null) currentTask.Output = ParseOutputToJsonElement(outcome.Output);
            var taskResult = new TaskResult("failed", outcome.Detail ?? outcome.Output);
            events.AddRange(run.FailTask(taskResult));
        }

        return events;
    }

    /// <summary>
    /// Applies check results. The first repairable failure schedules repair
    /// tasks and stops adjudicating later checks in the batch.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowEvent>> ProcessCheckOutcomeAsync(WorkflowRun run, CheckOutcome outcome)
    {
        var stage = run.CurrentStageId!;
        var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(_owner.GrainKey, stage);
        var actions = new List<CheckResultAction>(outcome.Results.Count);

        foreach (var cr in outcome.Results)
        {
            if (cr.Status == "pass")
            {
                actions.Add(new(cr, "pass"));
            }
            else if (cr.Status == "pending")
            {
                actions.Add(new(cr, "pending"));
            }
            else
            {
                var repairTasks = ResolveRepairTasks(run, stageDef, cr.Name, cr);
                actions.Add(repairTasks is not null
                    ? new(cr, "repair", repairTasks)
                    : new(cr, "fail"));
                if (repairTasks is not null)
                    break;
            }
        }

        return run.ProcessCheckResults(actions);
    }

    /// <summary>
    /// Resolves repair tasks for a failed check, honoring the repair budget
    /// unless the caller is explicitly bypassing it.
    /// </summary>
    public IReadOnlyList<TaskDefinition>? ResolveRepairTasks(
        WorkflowRun run,
        StageDefinition? stageDef,
        string checkName,
        CheckResult? result = null,
        bool enforceLimit = true)
    {
        var checkDef = stageDef?.Checks.Find(c => c.Name == checkName);
        if (checkDef?.OnFailure?.Repair is not { } repair) return null;

        if (enforceLimit)
        {
            var repairCount = run.GetRepairCount(checkName);
            if (repairCount >= repair.Limit) return null;
        }

        return run.BuildRepairTasks(checkName, repair, result);
    }

    /// <summary>
    /// Retry is the escape hatch from <see cref="FailureReason.CheckUnrepaired"/>,
    /// so it schedules the requested repair without consuming the normal budget.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowEvent>?> TryScheduleRequestedCheckRepairAsync(WorkflowRun run)
    {
        if (run.Status != WorkflowRunStatus.Failed)
            return null;

        var failure = run.Failure;
        if (failure?.Reason != FailureReason.CheckUnrepaired || string.IsNullOrWhiteSpace(failure.CheckName))
            return null;

        var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(_owner.GrainKey, failure.Stage);
        var repairTasks = ResolveRepairTasks(run, stageDef, failure.CheckName, enforceLimit: false);
        if (repairTasks is null)
            return null;

        ResetChecksRunningState(run);
        return run.ScheduleCheckRepair(failure.CheckName, repairTasks, failure.Message);
    }

    /// <summary>
    /// Clears running task/check state when execution is abandoned.
    /// </summary>
    public async Task ClearExecutableStateAsync(WorkflowRun run, string reason)
    {
        await _owner.ReleaseCurrentStageLocks(reason);

        var currentStage = run.CurrentStage();
        if (currentStage is not null)
        {
            ResetChecksRunningState(run);
        }

        var runningTask = run.CurrentStage().RunningTask;
        if (runningTask is not null)
        {
            var events = run.FailTaskForStopped(reason);
            await _owner.SaveAsyncWithEvents(events);
            return;
        }

        await _owner.SaveAsync();
    }

    public async Task<string?> MarkTaskRunningAsync(
        WorkflowRun run,
        string logicalTaskId,
        string runnerId,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var current = run.CurrentStage();
        await _owner.SessionHealthGate.CheckAndEnforceAsync(
            logicalTaskId, current.Id, _owner.GrainKey, run,
            commitAsync, "dispatch", default);

        var currentTask = current.Tasks.FirstOrDefault(t => t.Id == logicalTaskId);
        if (currentTask?.Status == TaskRunStatus.Running)
        {
            _owner.SetLastKnownRunnerId(runnerId);
            return currentTask.WorkId ?? logicalTaskId;
        }

        var workId = logicalTaskId;
        var events = run.StartTask(workId, runnerId);
        await _owner.SaveAsyncWithEvents(events);
        foreach (var e in events)
            await _owner.DispatchEvent(e);

        _owner.SetLastKnownRunnerId(runnerId);
        return workId;
    }

    public string MarkChecksRunning(WorkflowRun run, string stage, IReadOnlyList<CheckItem> items)
    {
        var checksWorkId = ChecksWorkIdFor(stage);
        var currentStage = run.CurrentStage();
        currentStage.ChecksWorkId = checksWorkId;
        var now = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Pure projection used before claiming. Work ids are stable: task id or
    /// <c>checks-{stage}</c>.
    /// </summary>
    public WorkItem? BuildWorkItem(WorkflowRun run, WorkflowWork work)
    {
        switch (work.WorkType)
        {
            case "task":
            {
                var t = (WorkflowWork.TaskData)work.Data;
                return WorkItem.Task(
                    stage: work.Stage,
                    id: t.Id,
                    title: t.Title,
                    uses: t.Uses,
                    with: t.With,
                    artifacts: t.Artifacts,
                    setVars: t.SetVars,
                    recovery: t.Recovery);
            }
            case "checks":
            {
                var ch = (WorkflowWork.ChecksData)work.Data;
                return WorkItem.Checks(work.Stage, ChecksWorkIdFor(work.Stage), ch.Items);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Transitions the resolved work item to Running. Re-claiming already
    /// running work returns the in-flight id.
    /// </summary>
    public async Task<string?> ClaimWorkItemAsync(
        WorkflowRun run,
        string workId,
        string runnerId,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync)
    {
        var currentStage = run.CurrentStage();

        var task = currentStage.Tasks.FirstOrDefault(t => t.Id == workId);
        if (task is not null)
        {
            if (task.Status == TaskRunStatus.Running)
            {
                _owner.SetLastKnownRunnerId(runnerId);
                return task.WorkId ?? task.Id;
            }
            if (task.Status != TaskRunStatus.Pending) return null;

            var claimedWorkId = await MarkTaskRunningAsync(run, task.Id, runnerId, commitAsync);
            return claimedWorkId;
        }

        if (workId == ChecksWorkIdFor(currentStage.Id))
        {
            if (!string.IsNullOrWhiteSpace(currentStage.ChecksWorkId))
            {
                _owner.SetLastKnownRunnerId(runnerId);
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
    /// Stable work id for the single checks batch a stage can have in flight.
    /// </summary>
    public static string ChecksWorkIdFor(string stage) => $"checks-{stage}";

    private static JsonElement? ParseOutputToJsonElement(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.Clone();
        }
        catch
        {
            var wrapped = JsonSerializer.SerializeToElement(output);
            return wrapped;
        }
    }

    /// <summary>
    /// Clears the current checks batch and returns any Running checks to Pending.
    /// </summary>
    public void ResetChecksRunningState(WorkflowRun run)
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
