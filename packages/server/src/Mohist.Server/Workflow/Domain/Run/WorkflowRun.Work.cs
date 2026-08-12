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

public sealed record WorkflowAgentResultSettlementTask(
    string Stage,
    TaskRun Task);

public static partial class WorkflowRunExtensions
{
    public static string ChecksWorkIdFor(string stage) => $"checks-{stage}";

    extension(WorkflowRun run)
    {
        public WorkflowWork? NextWork()
        {
            if (run.HasUnresolvedAgentResult()) return null;

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
                ? new WorkflowReportableTaskAttempt(
                    found.Stage.Id,
                    found.Task.Id,
                    found.Task.WorkId!,
                    found.Task.WorkerId!,
                    found.Task.AgentResultSettlement?.State)
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
                    && candidate.Task.AgentResultSettlement?.State is
                        AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked
                    && string.Equals(candidate.Task.WorkId, workId, StringComparison.Ordinal)
                    && string.Equals(candidate.Task.WorkerId, workerId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count != 1)
                return null;

            var match = matches[0];
            return ActiveTask(match.Stage, match.Task);
        }

        public WorkflowActiveWork? FindReportableWork(
            string taskRunId,
            string workId,
            string workerId)
        {
            var attempt = run.FindReportableTaskAttempt(taskRunId, workId, workerId);
            if (attempt is null)
                return null;

            var stage = run.Stages.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, attempt.Stage, StringComparison.Ordinal));
            var task = stage?.Tasks.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, attempt.TaskRunId, StringComparison.Ordinal));
            return stage is not null && task is not null
                ? ActiveTask(stage, task)
                : null;
        }

        /// <summary>
        /// Reconstructs only the declared report shape for pure ingress
        /// translation. Eligibility and Runner ownership are deliberately not
        /// considered here; the serialized grain turn owns that decision.
        /// </summary>
        public WorkItem? FindReportShape(string? taskRunId, string workId)
        {
            if (string.IsNullOrWhiteSpace(workId))
                return null;

            var currentStage = run.Stages.FirstOrDefault(stage =>
                string.Equals(stage.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(taskRunId))
            {
                var currentTask = currentStage?.Tasks.SingleOrDefault(task =>
                    string.Equals(task.Id, taskRunId, StringComparison.Ordinal)
                    && string.Equals(task.WorkId, workId, StringComparison.Ordinal));
                if (currentTask is not null)
                    return ActiveTask(currentStage!, currentTask).Item;

                var taskMatches = run.Stages
                    .SelectMany(stage => stage.Tasks.Select(task => (Stage: stage, Task: task)))
                    .Where(candidate =>
                        string.Equals(candidate.Task.Id, taskRunId, StringComparison.Ordinal)
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
            return checkMatches.Count == 1
                ? ActiveChecks(checkMatches[0])?.Item
                : null;
        }

        /// <summary>
        /// The single aggregate-level predicate for later dispatch and status
        /// decisions. Task and stage status enums intentionally do not mirror it.
        /// </summary>
        public bool HasUnresolvedAgentResult() =>
            run.Stages.SelectMany(stage => stage.Tasks)
                .Any(task => task.Status == TaskRunStatus.Running
                    && task.AgentResultSettlement?.State is AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked);

        public WorkflowAgentResultSettlementTask? FindAgentResultSettlementTask(
            AgentExecutionBinding binding)
        {
            var found = FindTaskAttempt(run, binding.TaskRunId, binding.WorkId, binding.RunnerId);
            return found is { } match && match.Task.AgentResultSettlement is not null
                ? new WorkflowAgentResultSettlementTask(match.Stage.Id, match.Task)
                : null;
        }

        public AgentExecutionBinding? FindBoundAgentExecution(
            string taskRunId,
            string workId,
            string runnerId)
        {
            var found = FindTaskAttempt(run, taskRunId, workId, runnerId);
            var settlement = found?.Task.AgentResultSettlement;
            return found is not null
                && found.Value.Task.Status == TaskRunStatus.Running
                && settlement is not null
                && HasFullExecutionBinding(settlement)
                ? new AgentExecutionBinding(
                    settlement.TaskRunId,
                    settlement.WorkId,
                    settlement.RunnerId,
                    settlement.AgentSessionId!,
                    settlement.AgentTurnId!,
                    settlement.Runtime!,
                    settlement.RuntimeSessionId!)
                : null;
        }

        public WorkflowAgentResultSettlementTask? FindUnresolvedAgentResultSettlementTask() =>
            run.Stages
                .SelectMany(stage => stage.Tasks.Select(task => new WorkflowAgentResultSettlementTask(stage.Id, task)))
                .SingleOrDefault(candidate => candidate.Task.Status == TaskRunStatus.Running
                    && candidate.Task.AgentResultSettlement?.State is AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked);

        public WorkflowAgentResultSettlementTask? FindCancelledAgentResultSettlementTask() =>
            run.Stages
                .SelectMany(stage => stage.Tasks.Select(task => new WorkflowAgentResultSettlementTask(stage.Id, task)))
                .SingleOrDefault(candidate => candidate.Task.Status == TaskRunStatus.Cancelled
                    && candidate.Task.AgentResultSettlement is not null);

        public WorkflowAgentResultSettlementTask? FindTerminalAgentResultSettlementTask() =>
            run.Stages
                .SelectMany(stage => stage.Tasks.Select(task => new WorkflowAgentResultSettlementTask(stage.Id, task)))
                .SingleOrDefault(candidate => candidate.Task.Status is TaskRunStatus.Completed or TaskRunStatus.Failed
                    && candidate.Task.AgentResultSettlement is not null);

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
            DateTimeOffset now,
            TimeSpan settlementTimeout)
        {
            if (settlementTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(settlementTimeout));
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

            return RecordObservation(
                settlement,
                observation.Kind,
                observation.ReasonCode,
                observation.Message,
                observation.StopOperationId,
                now,
                settlementTimeout);
        }

        public AgentExecutionUpdate ObserveAgentResultUnknown(
            string taskRunId,
            string workId,
            string runnerId,
            string reasonCode,
            string? message,
            DateTimeOffset now,
            TimeSpan settlementTimeout)
        {
            if (string.IsNullOrWhiteSpace(reasonCode)) return AgentExecutionUpdate.Rejected;
            var found = FindTaskAttempt(run, taskRunId, workId, runnerId);
            if (found is not { } match
                || match.Task.Status != TaskRunStatus.Running
                || match.Task.AgentResultSettlement is not { } settlement)
            {
                return AgentExecutionUpdate.Rejected;
            }

            return RecordObservation(
                settlement,
                AgentExecutionObservationKind.Unknown,
                reasonCode,
                message,
                stopOperationId: null,
                now,
                settlementTimeout);
        }

        public AgentExecutionUpdate ObserveAgentRunnerDisconnected(
            string runnerId,
            DateTimeOffset now,
            TimeSpan settlementTimeout)
        {
            var active = run.CurrentActiveWorkFor(runnerId);
            if (active is not { IsTask: true, TaskRunId: { } taskRunId })
                return AgentExecutionUpdate.Rejected;

            var found = FindTaskAttempt(run, taskRunId, active.WorkId, runnerId);
            if (found is not { } match
                || match.Task.Status != TaskRunStatus.Running
                || match.Task.AgentResultSettlement is not { } settlement)
            {
                return AgentExecutionUpdate.Rejected;
            }

            return RecordObservation(
                settlement,
                AgentExecutionObservationKind.Disconnected,
                "runner-disconnected",
                "Runner disconnected before the Agent result was accepted.",
                stopOperationId: null,
                now,
                settlementTimeout);
        }

        public IReadOnlyList<WorkflowEvent> BlockUnresolvedAgentResult(DateTimeOffset now)
        {
            var unresolved = run.FindUnresolvedAgentResultSettlementTask();
            var settlement = unresolved?.Task.AgentResultSettlement;
            if (unresolved is null
                || settlement?.State != AgentResultSettlementState.Unknown
                || settlement.DeadlineAt is not { } deadline
                || deadline > now)
            {
                return [];
            }

            settlement.State = AgentResultSettlementState.Blocked;
            const string reason = "agent-result-unconfirmed";
            return
            [
                new TaskBlocked(unresolved.Stage, unresolved.Task.Id, reason, deadline),
                new StageBlocked(unresolved.Stage, unresolved.Task.Id, reason),
                new WorkflowRunBlocked(unresolved.Stage, unresolved.Task.Id, reason, deadline)
            ];
        }

        public WorkflowPendingWork? CurrentPendingWork()
        {
            if (run.HasUnresolvedAgentResult()) return null;
            if (run.CurrentStageId is null) return null;
            if (run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running)) return null;

            var current = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (current is null) return null;
            var task = current.Tasks.FirstOrDefault(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed or TaskRunStatus.Cancelled));
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
            if (run.HasUnresolvedAgentResult())
                throw new InvalidOperationException("agent_result_unresolved");
            var current = run.CurrentStage();
            if (!current.Initialized)
                throw new InvalidOperationException($"Cannot add runtime task: stage {current.Id} is not initialized");
            if (!string.IsNullOrWhiteSpace(stage) && stage != current.Id)
                throw new InvalidOperationException("Cannot add runtime task to stage " + stage + "; current stage is " + current.Id);

            var runningIndex = current.Tasks.FindIndex(t => t.Status == TaskRunStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed or TaskRunStatus.Cancelled));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = TaskRun.MakeTask(
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
            DateTimeOffset now)
        {
            if (run.HasUnresolvedAgentResult())
                throw new InvalidOperationException("agent_result_unresolved");
            var current = run.CurrentStage();
            var runningIndex = current.Tasks.FindIndex(t => t.Status == TaskRunStatus.Running);
            var firstIncompleteIndex = current.Tasks.FindIndex(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed or TaskRunStatus.Cancelled));
            var insertIndex = runningIndex >= 0
                ? runningIndex + 1
                : firstIncompleteIndex >= 0
                    ? firstIncompleteIndex
                    : current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = task.RecoveryRemaining is { } remaining
                    ? TaskRun.MakeContinuationTask(
                        current.Tasks,
                        task.Definition,
                        current.Attempt,
                        remaining,
                        run.Stages.SelectMany(candidate => candidate.Tasks))
                    : TaskRun.MakeTask(
                        current.Tasks,
                        task.Definition,
                        current.Attempt,
                        run.Stages.SelectMany(candidate => candidate.Tasks));
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

    private static AgentExecutionUpdate RecordObservation(
        AgentResultSettlement settlement,
        AgentExecutionObservationKind kind,
        string reasonCode,
        string? message,
        string? stopOperationId,
        DateTimeOffset now,
        TimeSpan settlementTimeout)
    {
        var state = settlement.State == AgentResultSettlementState.AwaitingResult
            ? AgentResultSettlementState.Unknown
            : settlement.State;
        var firstUnknownAt = settlement.FirstUnknownAt ?? now;
        var deadlineAt = state == AgentResultSettlementState.Unknown
            ? settlement.DeadlineAt ?? firstUnknownAt + settlementTimeout
            : settlement.DeadlineAt;
        var operationId = stopOperationId ?? settlement.StopOperationId;
        if (settlement.State == state
            && settlement.FirstUnknownAt == firstUnknownAt
            && settlement.DeadlineAt == deadlineAt
            && settlement.LastObservation == kind
            && string.Equals(settlement.ReasonCode, reasonCode, StringComparison.Ordinal)
            && string.Equals(settlement.Message, message, StringComparison.Ordinal)
            && string.Equals(settlement.StopOperationId, operationId, StringComparison.Ordinal))
        {
            return AgentExecutionUpdate.Unchanged;
        }

        settlement.State = state;
        settlement.FirstUnknownAt = firstUnknownAt;
        settlement.DeadlineAt = deadlineAt;
        settlement.LastObservation = kind;
        settlement.ReasonCode = reasonCode;
        settlement.Message = message;
        settlement.StopOperationId = operationId;
        return AgentExecutionUpdate.Updated;
    }
}
