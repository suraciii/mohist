using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static class WorkflowOperations
{
    // === Lifecycle ===

    public static WorkflowRun Create(string id, WorkflowDefinition definition, WorkflowRunMetadata? metadata = null)
    {
        if (definition.Stages.Count == 0)
            throw new WorkflowDomainException("WorkflowDefinition requires at least one stage");

        var stages = definition.Stages
            .Select((def, i) => new StageRun(
                def.Stage,
                i,
                1,
                def.RequiresApproval,
                StageRunPhase.Pending,
                false,
                Array.Empty<TaskRun>(),
                Array.Empty<StageCheck>(),
                null,
                null))
            .ToList();

        return new WorkflowRun(
            id,
            metadata ?? new WorkflowRunMetadata(null, DateTimeOffset.UtcNow),
            WorkflowRunPhase.Pending,
            stages[0].StageId,
            stages,
            null,
            null,
            null);
    }

    public static WorkflowRun Start(WorkflowRun run)
    {
        if (run.Phase != WorkflowRunPhase.Pending && run.Phase != WorkflowRunPhase.Paused)
            throw new WorkflowDomainException($"WorkflowRun is {run.Phase}");

        var current = GetCurrentStage(run);
        var newCurrent = current.Phase == StageRunPhase.Pending
            ? current with { Phase = StageRunPhase.Running }
            : current;

        var updated = ReplaceStage(run, newCurrent);
        return updated with
        {
            Phase = WorkflowRunPhase.Running,
            StartedAt = run.StartedAt ?? DateTimeOffset.UtcNow
        };
    }

    public static WorkflowRun Pause(WorkflowRun run)
    {
        if (run.Phase != WorkflowRunPhase.Running)
            throw new WorkflowDomainException($"WorkflowRun is {run.Phase}, pause requires Running");
        return run with { Phase = WorkflowRunPhase.Paused };
    }

    public static WorkflowRun Resume(WorkflowRun run)
    {
        if (run.Phase != WorkflowRunPhase.Paused)
            throw new WorkflowDomainException($"WorkflowRun is {run.Phase}, resume requires Paused");

        var current = GetCurrentStage(run);
        var newCurrent = current.Phase == StageRunPhase.Pending
            ? current with { Phase = StageRunPhase.Running }
            : current;

        var updated = ReplaceStage(run, newCurrent);
        return updated with { Phase = WorkflowRunPhase.Running };
    }

    // === Stage Operations ===

    public static WorkflowRun InitStage(WorkflowRun run, IReadOnlyList<LoadedTaskInput> tasks, List<CheckDefinition> checks)
    {
        var current = GetCurrentStage(run);
        if (current.Initialized) return run;

        var pendingRuntimeTasks = current.Tasks
            .Where(t => t.Phase == TaskRunPhase.Pending)
            .Select(t => new LoadedTaskInput(t.DefinitionId, t.Title, t.Uses, t.WithInput))
            .ToList();

        var newTasks = new List<TaskRun>();
        foreach (var t in tasks)
            newTasks.Add(MakeTask(newTasks, t));
        foreach (var t in pendingRuntimeTasks)
            newTasks.Add(MakeTask(newTasks, t));

        var newChecks = checks
            .Select(c => new StageCheck(c.Name, c.Title, c.Uses, c.With, CheckRunPhase.Pending, 0, null, null))
            .ToList();

        var newStage = current with
        {
            Initialized = true,
            Tasks = newTasks,
            Checks = newChecks,
            Phase = StageRunPhase.Running
        };

        newStage = TryRequestApproval(newStage);

        var updated = ReplaceStage(run, newStage);
        return Advance(updated);
    }

    public static WorkflowRun Advance(WorkflowRun run)
    {
        while (true)
        {
            var current = GetCurrentStage(run);
            if (current.Phase != StageRunPhase.Completed) break;

            var nextStage = run.Stages
                .Where(s => s.Order > current.Order)
                .MinBy(s => s.Order);

            if (nextStage is null)
            {
                return run with
                {
                    Phase = WorkflowRunPhase.Completed,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }

            var startedNext = nextStage with { Phase = StageRunPhase.Running };
            run = ReplaceStage(run, startedNext) with { CurrentStageId = nextStage.StageId };
        }

        return RecomputeWorkflowPhase(run);
    }

    // === Task Operations ===

    public static WorkflowRun CompleteTask(WorkflowRun run)
    {
        var current = GetCurrentStage(run);
        var task = GetCurrentTask(current);
        if (task is null) return run;

        var newTask = task with { Phase = TaskRunPhase.Completed };
        var newStage = UpdateTask(current, newTask);
        newStage = TryRequestApproval(newStage);

        var updated = ReplaceStage(run, newStage);
        return Advance(updated);
    }

    public static WorkflowRun FailTask(WorkflowRun run, TaskResult result)
    {
        var current = GetCurrentStage(run);
        var task = GetCurrentTask(current);
        if (task is null) return run;

        var newTask = task with { Phase = TaskRunPhase.Failed };
        var newStage = UpdateTask(current, newTask) with
        {
            Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, task.Id, Message: result.Reason),
            Phase = StageRunPhase.Failed
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Failed };
    }

    public static WorkflowRun AddRuntimeTask(WorkflowRun run, LoadedTaskInput task, string? stage = null, bool invalidateChecks = false)
    {
        var current = GetCurrentStage(run);
        if (!string.IsNullOrWhiteSpace(stage) && stage != current.StageId)
            throw new WorkflowDomainException($"Cannot add runtime task to stage {stage}; current stage is {current.StageId}");

        var newTask = MakeTask(current.Tasks, task);
        var newTasks = current.Tasks.Append(newTask).ToList();

        var newChecks = invalidateChecks
            ? current.Checks.Select(c => c with { Phase = CheckRunPhase.Pending, Message = null, Output = (JsonElement?)null }).ToList()
            : current.Checks;

        var newStage = current with
        {
            Tasks = newTasks,
            Checks = newChecks,
            Failure = null,
            Approval = current.Approval?.Status == "awaiting" ? null : current.Approval,
            Phase = current.Initialized ? StageRunPhase.Running : current.Phase
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Running };
    }

    // === Check Operations ===

    public static WorkflowRun PassCheck(WorkflowRun run, CheckResult result)
    {
        var current = GetCurrentStage(run);
        var check = FindCheck(current, result.Name);

        var newCheck = check with
        {
            Phase = CheckRunPhase.Passed,
            Message = result.Message,
            Output = result.Output
        };

        var newStage = UpdateCheck(current, newCheck);
        newStage = TryRequestApproval(newStage);

        var updated = ReplaceStage(run, newStage);
        return Advance(updated);
    }

    public static WorkflowRun FailCheck(WorkflowRun run, CheckResult result)
    {
        var current = GetCurrentStage(run);
        var check = FindCheck(current, result.Name);

        var newCheck = check with
        {
            Phase = CheckRunPhase.Failed,
            Message = result.Message,
            Output = result.Output
        };

        var newStage = UpdateCheck(current, newCheck) with
        {
            Failure = new FailureDetails(FailureReason.CheckUnrepaired, current.StageId, CheckName: check.Name, Message: result.Message),
            Phase = StageRunPhase.Failed
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Failed };
    }

    public static WorkflowRun ResetCheck(WorkflowRun run, CheckResult result)
    {
        var current = GetCurrentStage(run);
        var check = FindCheck(current, result.Name);

        var newCheck = check with
        {
            Phase = CheckRunPhase.Pending,
            Message = result.Message,
            Output = result.Output
        };

        var newStage = UpdateCheck(current, newCheck);
        return ReplaceStage(run, newStage);
    }

    public static WorkflowRun PendingCheck(WorkflowRun run, CheckResult result)
        => ResetCheck(run, result);

    public static WorkflowRun InjectRetryTask(WorkflowRun run, string checkName, LoadedTaskInput task)
    {
        var current = GetCurrentStage(run);
        var newTask = MakeTask(current.Tasks, task);
        var newChecks = current.Checks.Select(c =>
            c.Name == checkName ? c with { RetryCount = c.RetryCount + 1 } : c).ToList();
        var newStage = current with
        {
            Tasks = current.Tasks.Append(newTask).ToList(),
            Checks = newChecks
        };
        return ReplaceStage(run, newStage);
    }

    public static WorkflowRun ClearStageFailure(WorkflowRun run)
    {
        var current = GetCurrentStage(run);
        var newStage = current with { Failure = null };
        return ReplaceStage(run, newStage);
    }

    // === Failure Handling ===

    public static WorkflowRun FailStage(WorkflowRun run, string reason)
    {
        var current = GetCurrentStage(run);
        var newStage = current with
        {
            Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, Message: reason),
            Phase = StageRunPhase.Failed
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Failed };
    }

    public static WorkflowRun FailInFlightWork(WorkflowRun run, string workType, string? reason)
    {
        var current = GetCurrentStage(run);

        switch (workType)
        {
            case "task":
            {
                var task = GetCurrentTask(current);
                if (task is null) return run;

                var newTask = task with { Phase = TaskRunPhase.Failed };
                var newStage = UpdateTask(current, newTask) with
                {
                    Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, task.Id, Message: reason),
                    Phase = StageRunPhase.Failed
                };
                var updated = ReplaceStage(run, newStage);
                return updated with { Phase = WorkflowRunPhase.Failed };
            }
            case "load":
            {
                var newStage = current with
                {
                    Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, Message: reason ?? "Task loading failed"),
                    Phase = StageRunPhase.Failed
                };
                var updated = ReplaceStage(run, newStage);
                return updated with { Phase = WorkflowRunPhase.Failed };
            }
            case "check" or "checks":
            {
                var pending = current.Checks.FirstOrDefault(c => c.Phase == CheckRunPhase.Pending);
                if (pending is null) return run;

                var newCheck = pending with { Phase = CheckRunPhase.Failed, Message = reason };
                var newStage = UpdateCheck(current, newCheck) with
                {
                    Failure = new FailureDetails(FailureReason.CheckUnrepaired, current.StageId, CheckName: pending.Name, Message: reason),
                    Phase = StageRunPhase.Failed
                };
                var updated = ReplaceStage(run, newStage);
                return updated with { Phase = WorkflowRunPhase.Failed };
            }
            default:
            {
                var newStage = current with
                {
                    Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, Message: reason ?? $"In-flight work lost (type={workType})"),
                    Phase = StageRunPhase.Failed
                };
                var updated = ReplaceStage(run, newStage);
                return updated with { Phase = WorkflowRunPhase.Failed };
            }
        }
    }

    public static WorkflowRun Retry(WorkflowRun run)
    {
        if (run.Phase != WorkflowRunPhase.Failed)
            throw new WorkflowDomainException($"WorkflowRun is {run.Phase}, retry requires failed");

        var current = GetCurrentStage(run);
        if (current.Failure is null)
            throw new WorkflowDomainException($"Stage {current.StageId} is not failed");

        return current.Failure.Reason switch
        {
            FailureReason.TaskFailed when current.Failure.TaskId is not null
                => RetryFailedTask(run, current, current.Failure.TaskId),
            FailureReason.TaskFailed
                => throw new WorkflowDomainException($"Stage {current.StageId} task failure has no task ID; use rerun to restart the stage"),
            FailureReason.CheckUnrepaired
                => RetryFailedCheck(run, current, current.Failure.CheckName),
            FailureReason.ApprovalRejected
                => throw new WorkflowDomainException($"Stage {current.StageId} failure is approval rejection; use rerun to restart the stage"),
            _ => throw new WorkflowDomainException($"Unknown failure reason: {current.Failure.Reason}")
        };
    }

    public static WorkflowRun Rerun(WorkflowRun run)
    {
        var current = GetCurrentStage(run);
        var newStage = new StageRun(
            current.StageId,
            current.Order,
            current.Attempt + 1,
            current.RequiresApproval,
            StageRunPhase.Running,
            false,
            Array.Empty<TaskRun>(),
            Array.Empty<StageCheck>(),
            null,
            null);

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Running };
    }

    // === Approval ===

    public static WorkflowRun Approve(WorkflowRun run, ApprovalInput? input = null)
    {
        var current = GetCurrentStage(run);
        if (current.Approval?.Status != "awaiting")
            throw new WorkflowDomainException($"Stage {current.StageId} is not awaiting approval");

        var newStage = current with
        {
            Approval = new ApprovalState("approved", input?.Output ?? null, current.Approval!.RequestedAt, DateTimeOffset.UtcNow.ToString("O")),
            Phase = StageRunPhase.Completed
        };

        var updated = ReplaceStage(run, newStage);
        return Advance(updated);
    }

    public static WorkflowRun Reject(WorkflowRun run, ApprovalInput? input = null)
    {
        var current = GetCurrentStage(run);
        if (current.Approval?.Status != "awaiting")
            throw new WorkflowDomainException($"Stage {current.StageId} is not awaiting approval");

        var message = input?.Output?.GetString();
        var newStage = current with
        {
            Approval = new ApprovalState("rejected", input?.Output ?? null, current.Approval!.RequestedAt, DateTimeOffset.UtcNow.ToString("O")),
            Failure = new FailureDetails(FailureReason.ApprovalRejected, current.StageId, Message: message),
            Phase = StageRunPhase.Failed
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Failed };
    }

    // === Queries ===

    public static WorkflowWork? GetNextWork(WorkflowRun run)
    {
        if (run.Phase != WorkflowRunPhase.Running) return null;

        var current = GetCurrentStage(run);
        if (current.Phase != StageRunPhase.Running) return null;

        if (!current.Initialized)
            return new WorkflowWork.StageInit(current.StageId);

        var task = GetCurrentTask(current);
        if (task is not null)
            return new WorkflowWork.Task(current.StageId, task.Id, task.Title, task.Uses, task.WithInput);

        var pendingChecks = current.Checks
            .Where(c => c.Phase == CheckRunPhase.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        if (pendingChecks.Count > 0)
            return new WorkflowWork.Checks(current.StageId, pendingChecks);

        return null;
    }

    public static bool HasIncompleteTaskUsing(WorkflowRun run, string uses)
    {
        var current = GetCurrentStage(run);
        return current.Tasks.Any(t => t.Uses == uses && t.Phase is TaskRunPhase.Pending or TaskRunPhase.Running);
    }

    public static bool HasIncompleteTaskId(WorkflowRun run, string id)
    {
        var current = GetCurrentStage(run);
        return current.Tasks.Any(t => t.DefinitionId == id && t.Phase is TaskRunPhase.Pending or TaskRunPhase.Running);
    }

    public static int RetryCountForCheck(WorkflowRun run, string checkName)
    {
        var current = GetCurrentStage(run);
        var check = current.Checks.FirstOrDefault(c => c.Name == checkName)
            ?? throw new InvalidOperationException($"Check {checkName} not found in current stage");
        return check.RetryCount;
    }

    // === Metadata ===

    public static WorkflowRun PatchMetadata(WorkflowRun run, WorkflowRunMetadata patch)
    {
        return run with
        {
            Metadata = new WorkflowRunMetadata(
                patch.Name ?? run.Metadata.Name,
                run.Metadata.CreatedAt,
                MergeDic(run.Metadata.Labels, patch.Labels),
                MergeDic(run.Metadata.Annotations, patch.Annotations))
        };
    }

    // === Private Helpers ===

    private static StageRun GetCurrentStage(WorkflowRun run)
    {
        if (run.CurrentStageId is null)
            throw new WorkflowDomainException("WorkflowRun has no current stage");
        return run.Stages.FirstOrDefault(s => s.StageId == run.CurrentStageId)
            ?? throw new WorkflowDomainException($"Current stage {run.CurrentStageId} not found");
    }

    private static WorkflowRun ReplaceStage(WorkflowRun run, StageRun stage)
    {
        var stages = run.Stages.Select(s => s.StageId == stage.StageId ? stage : s).ToList();
        return run with { Stages = stages };
    }

    private static TaskRun? GetCurrentTask(StageRun stage)
        => stage.Tasks.FirstOrDefault(t => t.Phase is not (TaskRunPhase.Completed or TaskRunPhase.Failed));

    private static StageRun UpdateTask(StageRun stage, TaskRun task)
    {
        var tasks = stage.Tasks.Select(t => t.Id == task.Id ? task : t).ToList();
        return stage with { Tasks = tasks };
    }

    private static StageRun UpdateCheck(StageRun stage, StageCheck check)
    {
        var checks = stage.Checks.Select(c => c.Name == check.Name ? check : c).ToList();
        return stage with { Checks = checks };
    }

    private static StageCheck FindCheck(StageRun stage, string name)
        => stage.Checks.FirstOrDefault(c => c.Name == name)
            ?? throw new WorkflowDomainException($"Check {name} not found in stage {stage.StageId}");

    private static TaskRun MakeTask(IReadOnlyList<TaskRun> existing, LoadedTaskInput input)
    {
        var attempt = existing
                          .Where(t => t.DefinitionId == input.Id)
                          .Select(t => t.Attempt)
                          .DefaultIfEmpty(0)
                          .Max() + 1;
        return new TaskRun(input.Id, attempt, input.Title, input.Uses, input.With, TaskRunPhase.Pending);
    }

    private static bool IsStageComplete(StageRun stage)
    {
        if (!stage.Initialized) return false;
        var hasPendingTask = stage.Tasks.Any(t => t.Phase is not (TaskRunPhase.Completed or TaskRunPhase.Failed));
        if (hasPendingTask) return false;
        return stage.Checks.All(c => c.Phase == CheckRunPhase.Passed);
    }

    private static StageRun TryRequestApproval(StageRun stage)
    {
        if (stage.RequiresApproval && stage.Approval is null && IsStageComplete(stage))
        {
            return stage with
            {
                Approval = new ApprovalState("awaiting", null, DateTimeOffset.UtcNow.ToString("O"), null),
                Phase = StageRunPhase.AwaitingApproval
            };
        }

        return ComputeStagePhase(stage);
    }

    private static StageRun ComputeStagePhase(StageRun stage)
    {
        if (stage.Failure is not null)
            return stage with { Phase = StageRunPhase.Failed };

        if (stage.Approval?.Status == "awaiting")
            return stage with { Phase = StageRunPhase.AwaitingApproval };

        if (IsStageComplete(stage))
        {
            if (stage.RequiresApproval && stage.Approval?.Status != "approved")
                return stage with { Phase = StageRunPhase.Running };
            return stage with { Phase = StageRunPhase.Completed };
        }

        return stage with { Phase = StageRunPhase.Running };
    }

    private static WorkflowRun RecomputeWorkflowPhase(WorkflowRun run)
    {
        if (run.Phase is WorkflowRunPhase.Pending or WorkflowRunPhase.Paused)
            return run;

        var current = GetCurrentStage(run);

        if (current.Phase == StageRunPhase.Failed)
            return run with { Phase = WorkflowRunPhase.Failed };

        if (current.Phase == StageRunPhase.AwaitingApproval)
            return run with { Phase = WorkflowRunPhase.AwaitingApproval };

        if (current.Phase == StageRunPhase.Completed && run.Stages.Count > 0 && run.Stages[^1].StageId == current.StageId)
            return run with { Phase = WorkflowRunPhase.Completed, CompletedAt = DateTimeOffset.UtcNow };

        return run with { Phase = WorkflowRunPhase.Running };
    }

    private static WorkflowRun RetryFailedTask(WorkflowRun run, StageRun stage, string taskRunId)
    {
        var failedTask = stage.Tasks.LastOrDefault(t => t.Id == taskRunId && t.Phase == TaskRunPhase.Failed)
            ?? throw new WorkflowDomainException($"Failed task {taskRunId} not found or not in failed state");

        var input = new LoadedTaskInput(failedTask.DefinitionId, failedTask.Title, failedTask.Uses, failedTask.WithInput);
        var newTask = MakeTask(stage.Tasks, input);

        var newStage = stage with
        {
            Tasks = stage.Tasks.Append(newTask).ToList(),
            Failure = null,
            Phase = StageRunPhase.Running
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Running };
    }

    private static WorkflowRun RetryFailedCheck(WorkflowRun run, StageRun stage, string? checkName)
    {
        var failedCheck = stage.Checks.FirstOrDefault(c => c.Name == checkName && c.Phase == CheckRunPhase.Failed)
            ?? throw new WorkflowDomainException($"Failed check {checkName} not found or not in failed state");

        var resetCheck = failedCheck with
        {
            Phase = CheckRunPhase.Pending,
            Message = null,
            Output = (JsonElement?)null
        };

        var newStage = UpdateCheck(stage, resetCheck) with
        {
            Failure = null,
            Phase = StageRunPhase.Running
        };

        var updated = ReplaceStage(run, newStage);
        return updated with { Phase = WorkflowRunPhase.Running };
    }

    private static Dictionary<string, string>? MergeDic(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (a is null && b is null) return null;
        var result = new Dictionary<string, string>();
        if (a is not null)
            foreach (var kv in a) result[kv.Key] = kv.Value;
        if (b is not null)
            foreach (var kv in b) result[kv.Key] = kv.Value;
        return result;
    }
}
