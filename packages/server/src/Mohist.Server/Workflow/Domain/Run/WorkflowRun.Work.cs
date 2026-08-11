using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Runner.Grains;

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

public sealed record WorkflowActiveWork(WorkItem Item, string? TaskRunId)
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

public static partial class WorkflowRunExtensions
{
    public static string ChecksWorkIdFor(string stage) => $"checks-{stage}";

    extension(WorkflowRun run)
    {
        public WorkflowWork? NextWork()
        {
            var current = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (current is null) return null;

            var pendingTask = NextUnclaimedTask(current);
            if (pendingTask is not null)
                return new WorkflowTaskWork(current.Id, pendingTask.Id, pendingTask.Title, pendingTask.Uses, pendingTask.WithInput, pendingTask.ExpectInput, pendingTask.Artifacts, pendingTask.SetVars, pendingTask.Recovery, pendingTask.RecoveryRemaining);

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
            string taskRunId,
            string workId,
            string workerId)
        {
            var match = FindTaskAttempt(run, taskRunId, workId, workerId);
            return match is { } found && found.Task.Status == TaskRunStatus.Running
                ? new WorkflowReportableTaskAttempt(found.Stage.Id, found.Task.Id, found.Task.WorkId!, found.Task.WorkerId!)
                : null;
        }

        public WorkflowActiveWork? FindReportableWork(string workId, string workerId)
        {
            if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(workerId))
                return null;

            var active = run.FindActiveWork(workId, workerId);
            if (active is not null)
                return active;

            var matches = run.Stages
                .SelectMany(stage => stage.Tasks.Select(task => (Stage: stage, Task: task)))
                .Where(candidate =>
                    candidate.Task.Status == TaskRunStatus.Running
                    && string.Equals(candidate.Task.WorkId, workId, StringComparison.Ordinal)
                    && string.Equals(candidate.Task.WorkerId, workerId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count != 1)
                return null;

            var match = matches[0];
            return ActiveTask(match.Stage, match.Task);
        }

        /// <summary>
        /// The single aggregate-level predicate for later dispatch and status
        /// decisions. Task and stage status enums intentionally do not mirror it.
        /// </summary>
        public bool HasUnresolvedAgentResult() =>
            run.Stages.SelectMany(stage => stage.Tasks)
                .Any(task => task.AgentResultSettlement is not null);

        public AgentExecutionUpdate BindAgentExecution(AgentExecutionBinding binding)
        {
            if (!IsValid(binding))
                return AgentExecutionUpdate.Rejected;

            var match = FindTaskAttempt(run, binding.TaskRunId, binding.WorkId, binding.RunnerId);
            if (match is not { } found
                || found.Task.Status != TaskRunStatus.Running
                || found.Task.AgentResultSettlement is not { } settlement)
            {
                return AgentExecutionUpdate.Rejected;
            }

            if (!MatchesAttempt(settlement, binding) || !MatchesBoundFields(settlement, binding))
                return AgentExecutionUpdate.Rejected;

            if (HasFullExecutionBinding(settlement))
                return AgentExecutionUpdate.Unchanged;

            settlement.AgentSessionId = binding.AgentSessionId;
            settlement.AgentTurnId = binding.AgentTurnId;
            settlement.Runtime = binding.Runtime;
            settlement.RuntimeSessionId = binding.RuntimeSessionId;
            return AgentExecutionUpdate.Updated;
        }

        public AgentExecutionUpdate ObserveAgentExecution(
            AgentExecutionObservation observation,
            DateTimeOffset now)
        {
            if (!IsValid(observation.Binding) || string.IsNullOrWhiteSpace(observation.ReasonCode))
                return AgentExecutionUpdate.Rejected;

            var binding = observation.Binding;
            var match = FindTaskAttempt(run, binding.TaskRunId, binding.WorkId, binding.RunnerId);
            if (match is not { } found
                || found.Task.Status != TaskRunStatus.Running
                || found.Task.AgentResultSettlement is not { } settlement
                || !MatchesAttempt(settlement, binding)
                || !HasFullExecutionBinding(settlement)
                || !MatchesBoundFields(settlement, binding)
                || (settlement.StopOperationId is not null
                    && observation.StopOperationId is not null
                    && !string.Equals(settlement.StopOperationId, observation.StopOperationId, StringComparison.Ordinal)))
            {
                return AgentExecutionUpdate.Rejected;
            }

            var state = settlement.State == AgentResultSettlementState.AwaitingResult
                ? AgentResultSettlementState.Unknown
                : settlement.State;
            var firstUnknownAt = settlement.FirstUnknownAt ?? now;
            var stopOperationId = observation.StopOperationId ?? settlement.StopOperationId;
            if (settlement.State == state
                && settlement.FirstUnknownAt == firstUnknownAt
                && settlement.LastObservation == observation.Kind
                && string.Equals(settlement.ReasonCode, observation.ReasonCode, StringComparison.Ordinal)
                && string.Equals(settlement.Message, observation.Message, StringComparison.Ordinal)
                && string.Equals(settlement.StopOperationId, stopOperationId, StringComparison.Ordinal))
            {
                return AgentExecutionUpdate.Unchanged;
            }

            settlement.State = state;
            settlement.FirstUnknownAt = firstUnknownAt;
            settlement.LastObservation = observation.Kind;
            settlement.ReasonCode = observation.ReasonCode;
            settlement.Message = observation.Message;
            settlement.StopOperationId = stopOperationId;
            return AgentExecutionUpdate.Updated;
        }

        public WorkflowPendingWork? CurrentPendingWork()
        {
            if (run.CurrentStageId is null) return null;
            if (run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running)) return null;

            var current = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (current is null) return null;
            var task = current.Tasks.FirstOrDefault(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            if (task is not null)
                return new WorkflowPendingWork(task.Id, WorkItemTypes.Task, current.Id, task.Title);

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

            var runningIndex = current.Tasks.FindIndex(t => t.Status == TaskRunStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = TaskRun.MakeTask(current.Tasks, task, current.Attempt, causedByFeedbackId);
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
            DateTimeOffset now)
        {
            var current = run.CurrentStage();
            var runningIndex = current.Tasks.FindIndex(t => t.Status == TaskRunStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = task.RecoveryRemaining is { } remaining
                    ? TaskRun.MakeContinuationTask(current.Tasks, task.Definition, current.Attempt, remaining)
                    : TaskRun.MakeTask(current.Tasks, task.Definition, current.Attempt);
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
            return current.Tasks.Any(t => t.Uses == uses && t.Status != TaskRunStatus.Completed);
        }

        public bool HasIncompleteTaskById(string id)
        {
            var current = run.CurrentStage();
            return current.Tasks.Any(t => t.Id == id && t.Status != TaskRunStatus.Completed);
        }
    }

    private static WorkflowActiveWork ActiveTask(StageRun stage, TaskRun task)
    {
        var workId = task.WorkId ?? task.Id;
        var item = WorkItem.Task(
            stage.Id, workId, task.Title, task.Uses,
            task.WithInput, task.Artifacts, task.SetVars, task.Recovery, task.RecoveryRemaining,
            task.ExpectInput);
        return new WorkflowActiveWork(item, task.Id);
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
            TaskRunId: null);
    }

    private static TaskRun? NextUnclaimedTask(StageRun stage) =>
        stage.Tasks.FirstOrDefault(t => t.Status == TaskRunStatus.Pending);

    private static (StageRun Stage, TaskRun Task)? FindTaskAttempt(
        WorkflowRun run,
        string taskRunId,
        string workId,
        string workerId)
    {
        if (string.IsNullOrWhiteSpace(taskRunId)
            || string.IsNullOrWhiteSpace(workId)
            || string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        foreach (var stage in run.Stages)
        {
            var task = stage.Tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, taskRunId, StringComparison.Ordinal)
                && string.Equals(candidate.WorkId, workId, StringComparison.Ordinal)
                && string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal));
            if (task is not null)
                return (stage, task);
        }

        return null;
    }

    private static bool IsValid(AgentExecutionBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.TaskRunId)
        && !string.IsNullOrWhiteSpace(binding.WorkId)
        && !string.IsNullOrWhiteSpace(binding.RunnerId)
        && !string.IsNullOrWhiteSpace(binding.AgentSessionId)
        && !string.IsNullOrWhiteSpace(binding.AgentTurnId)
        && !string.IsNullOrWhiteSpace(binding.Runtime)
        && !string.IsNullOrWhiteSpace(binding.RuntimeSessionId);

    private static bool MatchesAttempt(AgentResultSettlement settlement, AgentExecutionBinding binding) =>
        string.Equals(settlement.TaskRunId, binding.TaskRunId, StringComparison.Ordinal)
        && string.Equals(settlement.WorkId, binding.WorkId, StringComparison.Ordinal)
        && string.Equals(settlement.RunnerId, binding.RunnerId, StringComparison.Ordinal);

    private static bool HasFullExecutionBinding(AgentResultSettlement settlement) =>
        settlement.AgentSessionId is not null
        && settlement.AgentTurnId is not null
        && settlement.Runtime is not null
        && settlement.RuntimeSessionId is not null;

    private static bool MatchesBoundFields(AgentResultSettlement settlement, AgentExecutionBinding binding) =>
        (settlement.AgentSessionId is null || string.Equals(settlement.AgentSessionId, binding.AgentSessionId, StringComparison.Ordinal))
        && (settlement.AgentTurnId is null || string.Equals(settlement.AgentTurnId, binding.AgentTurnId, StringComparison.Ordinal))
        && (settlement.Runtime is null || string.Equals(settlement.Runtime, binding.Runtime, StringComparison.Ordinal))
        && (settlement.RuntimeSessionId is null || string.Equals(settlement.RuntimeSessionId, binding.RuntimeSessionId, StringComparison.Ordinal));
}
