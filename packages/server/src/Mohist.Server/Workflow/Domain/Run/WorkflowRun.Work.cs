using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Domain.Run;

public abstract record WorkflowWork(string Stage);

public sealed record WorkflowTaskWork(
    string Stage,
    string Id,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With,
    Dictionary<string, JsonElement?>? Expect = null,
    TaskArtifactCapture? Artifacts = null,
    Dictionary<string, string>? SetVars = null,
    RecoveryDefinition? Recovery = null,
    int? RecoveryRemaining = null) : WorkflowWork(Stage);

public sealed record WorkflowChecksWork(
    string Stage,
    List<CheckItem> Items) : WorkflowWork(Stage);

public sealed record WorkflowActiveWork(WorkItem Item, string? ActionAttemptId, string? ProcessGeneration)
{
    public string WorkId => Item.Id ?? string.Empty;
    public bool IsTask => Item.IsTask;
    public bool IsChecks => Item.IsChecks;
}

public sealed record WorkflowPendingWork(
    string Id,
    string WorkType,
    string Stage,
    string Title);

public sealed record WorkflowReportableTaskAttempt(
    string Stage,
    string ActionAttemptId,
    string WorkId,
    string RunnerId);

public static partial class WorkflowRunExtensions
{
    public static string ChecksWorkIdFor(string stage) => $"checks-{stage}";

    extension(WorkflowRun run)
    {
        public WorkflowWork? NextWork()
        {
            var current = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (current is null) return null;

            if (current.Tasks.Any(task => task.Status == WorkflowActionAttemptStatus.Running
                && string.Equals(task.Uses, "mohist/agent", StringComparison.Ordinal)))
                return null;

            var pendingTask = NextUnclaimedTask(current);
            if (pendingTask is not null)
            {
                // A blocked lane is an ordered-stage barrier. Do not fall
                // through to checks or any other later work while recovery is
                // still waiting on the first non-passing lane.
                if (!VerificationLaneGate.IsClaimableLaneTask(run, pendingTask))
                    return null;

                return new WorkflowTaskWork(current.Id, pendingTask.Id, pendingTask.Title, pendingTask.Uses, pendingTask.WithInput, pendingTask.ExpectInput, pendingTask.Artifacts, pendingTask.SetVars, pendingTask.Recovery, pendingTask.RecoveryRemaining);
            }

            // A lane-enabled stage with no claimable task may still have a
            // failed, timed-out, or missing lane and pending checks. Checks
            // must not bypass the all-lanes-pass gate.
            if (string.Equals(current.Id, "build", StringComparison.Ordinal)
                && VerificationLaneGate.IsLaneEnabledRun(run)
                && !VerificationLaneGate.CanAdvanceBuildStage(run))
            {
                return null;
            }

            var pendingChecks = current.Checks
                .Where(c => c.Status == StageCheckStatus.Pending)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();

            if (pendingChecks.Any())
                return new WorkflowChecksWork(current.Id, pendingChecks);

            return null;
        }

        public WorkflowActiveWork? CurrentActiveWorkFor(string workerId)
        {
            if (!run.IsAssignedTo(workerId)) return null;

            var current = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (current is null) return null;
            var task = current.RunningTask;
            if (task is not null)
            {
                if (!string.Equals(task.WorkerId, workerId, StringComparison.Ordinal))
                    return null;

                return ActiveTask(current, task);
            }

            return ActiveChecks(current);
        }

        public WorkflowActiveWork? FindActiveWork(string workId, string workerId)
        {
            if (string.IsNullOrWhiteSpace(workId)) return null;

            var active = run.CurrentActiveWorkFor(workerId);
            return active is not null && string.Equals(active.WorkId, workId, StringComparison.Ordinal)
                ? active
                : null;
        }

        /// <summary>
        /// Finds a task attempt that may still accept an authoritative report.
        /// The persisted task-run, work, and runner tuple is the identity; a
        /// settlement state never substitutes for any part of that tuple.
        /// </summary>
        public WorkflowReportableTaskAttempt? FindReportableTaskAttempt(
            string actionAttemptId,
            string workId,
            string workerId)
        {
            var match = FindTaskAttempt(run, actionAttemptId, workId, workerId);
            return match is { } found
                && found.Task.Status == WorkflowActionAttemptStatus.Running
                ? new WorkflowReportableTaskAttempt(
                    found.Stage.Id,
                    found.Task.Id,
                    found.Task.WorkId!,
                    found.Task.WorkerId!)
                : null;
        }

        public WorkflowActiveWork? FindReportableWork(string workId, string workerId)
        {
            if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(workerId))
                return null;

            return run.FindActiveWork(workId, workerId);
        }

        public WorkflowActiveWork? FindReportableWork(
            string actionAttemptId,
            string workId,
            string workerId)
        {
            var attempt = run.FindReportableTaskAttempt(actionAttemptId, workId, workerId);
            if (attempt is null)
                return null;

            var stage = run.Stages.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, attempt.Stage, StringComparison.Ordinal));
            var task = stage?.Tasks.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, attempt.ActionAttemptId, StringComparison.Ordinal));
            return stage is not null && task is not null
                ? ActiveTask(stage, task)
                : null;
        }

        /// <summary>
        /// Finds the exact terminal attempt for an idempotent report replay.
        /// The worker identity remains part of the attempt fence even after
        /// terminalization; callers must still compare the stored result
        /// fingerprint before acknowledging a replay.
        /// </summary>
        public WorkflowActiveWork? FindTerminalReportAttempt(
            string actionAttemptId,
            string workId,
            string workerId)
        {
            if (string.IsNullOrWhiteSpace(actionAttemptId)
                || string.IsNullOrWhiteSpace(workId)
                || string.IsNullOrWhiteSpace(workerId))
                return null;

            foreach (var stage in run.Stages)
            {
                var task = stage.Tasks.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, actionAttemptId, StringComparison.Ordinal)
                    && string.Equals(candidate.WorkId, workId, StringComparison.Ordinal)
                    && string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal)
                    && candidate.Status is WorkflowActionAttemptStatus.Completed or WorkflowActionAttemptStatus.Failed);
                if (task is not null)
                    return ActiveTask(stage, task);
            }

            return null;
        }

        /// <summary>
        /// Reconstructs only the declared report shape for pure ingress
        /// translation. Eligibility and Runner ownership are deliberately not
        /// considered here; the serialized grain turn owns that decision.
        /// </summary>
        public WorkItem? FindReportShape(string? actionAttemptId, string workId)
        {
            if (string.IsNullOrWhiteSpace(workId))
                return null;

            var currentStage = run.Stages.FirstOrDefault(stage =>
                string.Equals(stage.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(actionAttemptId))
            {
                var currentTask = currentStage?.Tasks.SingleOrDefault(task =>
                    string.Equals(task.Id, actionAttemptId, StringComparison.Ordinal)
                    && string.Equals(task.WorkId, workId, StringComparison.Ordinal));
                if (currentTask is not null)
                    return ActiveTask(currentStage!, currentTask).Item;

                var taskMatches = run.Stages
                    .SelectMany(stage => stage.Tasks.Select(task => (Stage: stage, Task: task)))
                    .Where(candidate =>
                        string.Equals(candidate.Task.Id, actionAttemptId, StringComparison.Ordinal)
                        && string.Equals(candidate.Task.WorkId, workId, StringComparison.Ordinal))
                    .Take(2)
                    .ToList();
                return taskMatches.Count == 1
                    ? ActiveTask(taskMatches[0].Stage, taskMatches[0].Task).Item
                    : null;
            }

            var checkMatches = run.Stages
                .Where(stage => string.Equals(stage.ChecksWorkId, workId, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (checkMatches.Count == 1)
                return ActiveChecks(checkMatches[0])?.Item;

            var terminalCheckMatches = run.Stages
                .Where(stage => string.Equals(stage.TerminalChecksWorkId, workId, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (terminalCheckMatches.Count != 1)
                return null;
            var terminalStage = terminalCheckMatches[0];
            var checks = terminalStage.Checks
                .Select(check => new CheckItem(check.Name, check.Title, check.Uses, check.WithInput))
                .ToList();
            return WorkItem.Checks(terminalStage.Id, workId, checks);
        }

        public WorkflowPendingWork? CurrentPendingWork()
        {
            if (run.CurrentStageId is null) return null;
            if (run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running)) return null;

            var current = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (current is null) return null;
            var task = current.Tasks.FirstOrDefault(t => t.Status is not (WorkflowActionAttemptStatus.Completed or WorkflowActionAttemptStatus.Failed or WorkflowActionAttemptStatus.Cancelled));
            if (task is not null)
            {
                if (!VerificationLaneGate.IsClaimableLaneTask(run, task))
                    return null;
                return new WorkflowPendingWork(task.WorkId ?? task.Id, WorkItemTypes.Task, current.Id, task.Title);
            }

            if (string.Equals(current.Id, "build", StringComparison.Ordinal)
                && VerificationLaneGate.IsLaneEnabledRun(run)
                && !VerificationLaneGate.CanAdvanceBuildStage(run))
            {
                return null;
            }

            if (current.Checks.Count > 0 && current.Checks.Any(c => c.Status != StageCheckStatus.Passed))
                return new WorkflowPendingWork("checks", WorkItemTypes.Checks, current.Id, "Checks");

            return null;
        }

        public IReadOnlyList<WorkflowEvent> AddRuntimeTask(
            TaskDefinition task,
            DateTimeOffset now,
            string? stage = null,
            bool invalidateChecks = false,
            string? causedByFeedbackId = null)
            => run.AddRuntimeTasks([task], now, stage, invalidateChecks, causedByFeedbackId);

        public IReadOnlyList<WorkflowEvent> AddRuntimeTasks(
            IReadOnlyList<TaskDefinition> tasks,
            DateTimeOffset now,
            string? stage = null,
            bool invalidateChecks = false,
            string? causedByFeedbackId = null)
        {
            var current = run.CurrentStage();
            if (!current.Initialized)
                throw new InvalidOperationException($"Cannot add runtime task: stage {current.Id} is not initialized");
            if (!string.IsNullOrWhiteSpace(stage) && stage != current.Id)
                throw new InvalidOperationException("Cannot add runtime task to stage " + stage + "; current stage is " + current.Id);

            var runningIndex = current.Tasks.FindIndex(t => t.Status == WorkflowActionAttemptStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (WorkflowActionAttemptStatus.Completed or WorkflowActionAttemptStatus.Failed or WorkflowActionAttemptStatus.Cancelled));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = WorkflowActionAttempt.MakeTask(
                    current.Tasks,
                    task,
                    current.Attempt,
                    run.Stages.SelectMany(candidate => candidate.Tasks),
                    causedByFeedbackId);
                current.Tasks.Insert(insertIndex, newTask);
                insertIndex++;
            }

            if (invalidateChecks)
            {
                foreach (var c in current.Checks)
                {
                    c.Status = StageCheckStatus.Pending;
                    c.Message = null;
                    c.Output = null;
                }
            }

            current.Failure = null;
            if (current.IsAwaitingApproval)
                current.ApprovalStatus = null;
            if (current.Initialized)
                current.Status = StageRunStatus.Running;

            if (run.Status == WorkflowRunStatus.Paused)
                return [];

            ApplyActiveOrWaitingForDispatchStatus(run, now);
            return tasks.Count > 0 ? [new WorkflowRunResumed()] : [];
        }

        internal IReadOnlyList<WorkflowEvent> AddRuntimeTaskAttempts(
            IReadOnlyList<(TaskDefinition Definition, int? RecoveryRemaining)> tasks,
            DateTimeOffset now,
            string? causedByFailedTaskId = null)
        {
            var current = run.CurrentStage();
            var sourceTask = causedByFailedTaskId is null
                ? null
                : current.Tasks.FirstOrDefault(task =>
                    string.Equals(task.Id, causedByFailedTaskId, StringComparison.Ordinal));

            // A recovery report is fenced by the source task's durable
            // identity. Keep the same fence at task insertion so a replayed
            // scheduling envelope cannot create a second repair/retry chain.
            if (sourceTask is not null
                && current.Tasks.Any(task =>
                    string.Equals(task.CausedByFailedTaskId, sourceTask.Id, StringComparison.Ordinal)))
            {
                return [];
            }

            var runningIndex = current.Tasks.FindIndex(t => t.Status == WorkflowActionAttemptStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (WorkflowActionAttemptStatus.Completed or WorkflowActionAttemptStatus.Failed or WorkflowActionAttemptStatus.Cancelled));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;
            var sourceLaneRetryAdded = false;

            foreach (var task in tasks)
            {
                if (sourceTask?.Lane is { } sourceLaneForEnvelope
                    && VerificationLaneCatalog.IsKnownLane(task.Definition.Id))
                {
                    // A recovery envelope for one lane may contain helpers,
                    // but it must not introduce another catalog lane or a
                    // second retry for the source identity. Otherwise a
                    // replayed or malformed envelope could run a later lane
                    // twice after the target retry passes.
                    if (!string.Equals(task.Definition.Id, sourceLaneForEnvelope.LaneId, StringComparison.Ordinal)
                        || sourceLaneRetryAdded)
                    {
                        continue;
                    }
                    sourceLaneRetryAdded = true;
                }

                // The Runner normally echoes the source lane definition. The
                // source remains authoritative for a recovery retry's lane
                // budget and recovery contract if a replayed envelope is
                // incomplete or was rendered by an older Runner.
                var definition = sourceTask?.Lane is not null
                    && string.Equals(task.Definition.Id, sourceTask.DefinitionId, StringComparison.Ordinal)
                    // The persisted source attempt owns the lane contract.
                    // Runner follow-ups carry a scheduling hint, not a new
                    // command, action, title, or recovery declaration.
                    ? sourceTask.ToDefinition()
                    : task.Definition;
                var newTask = task.RecoveryRemaining is { } remaining
                    ? WorkflowActionAttempt.MakeContinuationTask(
                        current.Tasks,
                        definition,
                        current.Attempt,
                        remaining,
                        run.Stages.SelectMany(candidate => candidate.Tasks),
                        causedByFailedTaskId: sourceTask?.Id)
                    : WorkflowActionAttempt.MakeTask(
                        current.Tasks,
                        definition,
                        current.Attempt,
                        run.Stages.SelectMany(candidate => candidate.Tasks),
                        causedByFailedTaskId: sourceTask?.Id);

                if (sourceTask?.Lane is { } sourceLane
                    && newTask.Lane is { } retryLane
                    && string.Equals(newTask.DefinitionId, sourceTask.DefinitionId, StringComparison.Ordinal))
                {
                    newTask.Lane = retryLane with
                    {
                        LaneId = sourceLane.LaneId,
                        Order = sourceLane.Order,
                        ConfiguredBudgetMs = sourceLane.ConfiguredBudgetMs,
                    };
                }

                current.Tasks.Insert(insertIndex, newTask);
                insertIndex++;
            }

            current.Failure = null;
            if (current.IsAwaitingApproval)
                current.ApprovalStatus = null;
            current.Status = StageRunStatus.Running;

            if (run.Status == WorkflowRunStatus.Paused)
                return [];

            ApplyActiveOrWaitingForDispatchStatus(run, now);
            return tasks.Count > 0 ? [new WorkflowRunResumed()] : [];
        }

        public bool HasIncompleteTaskWithUses(string uses)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Uses == uses && t.Status != WorkflowActionAttemptStatus.Completed);
        }

        public bool HasIncompleteTaskById(string id)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Id == id && t.Status != WorkflowActionAttemptStatus.Completed);
        }
    }

    private static WorkflowActiveWork ActiveTask(StageRun stage, WorkflowActionAttempt task)
    {
        var workId = task.WorkId ?? task.Id;
        var item = WorkItem.Task(
            stage.Id, workId, task.Title, task.Uses,
            task.WithInput, task.Artifacts, task.SetVars, task.Recovery, task.RecoveryRemaining,
            task.ExpectInput);
        return new WorkflowActiveWork(item, task.Id, task.ProcessGeneration);
    }

    private static WorkflowActiveWork? ActiveChecks(StageRun stage)
    {
        if (string.IsNullOrWhiteSpace(stage.ChecksWorkId))
            return null;

        var checks = stage.Checks
            .Where(c => c.Status is StageCheckStatus.Pending or StageCheckStatus.Running)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();

        return new WorkflowActiveWork(
            WorkItem.Checks(stage.Id, stage.ChecksWorkId, checks),
            ActionAttemptId: null,
            ProcessGeneration: stage.ChecksProcessGeneration);
    }

    private static WorkflowActionAttempt? NextUnclaimedTask(StageRun stage) =>
        stage.Tasks.FirstOrDefault(t => t.Status == WorkflowActionAttemptStatus.Pending);

    private static (StageRun Stage, WorkflowActionAttempt Task)? FindTaskAttempt(
        WorkflowRun run,
        string actionAttemptId,
        string workId,
        string workerId)
    {
        if (string.IsNullOrWhiteSpace(actionAttemptId)
            || string.IsNullOrWhiteSpace(workId)
            || string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        foreach (var stage in run.Stages)
        {
            var task = stage.Tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, actionAttemptId, StringComparison.Ordinal)
                && string.Equals(candidate.WorkId, workId, StringComparison.Ordinal)
                && string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal));
            if (task is not null)
                return (stage, task);
        }

        return null;
    }
}
