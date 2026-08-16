using Mohist.Server.Workflow.Domain;
using Mohist.Server.Runner.Domain;
namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> StartTask(string workId, string workerId, DateTimeOffset now)
        {
            if (run.Status is not (WorkflowRunStatus.Ready or WorkflowRunStatus.Running))
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, start task requires Ready or Running");
            run.RequireAssignedTo(workerId);

            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null) return [];

            task.Status = TaskRunStatus.Running;
            task.StartedAt = now;
            task.WorkId = workId;
            task.WorkerId = workerId;
            if (RequiresAgentResultSettlement(task.Uses))
            {
                task.AgentResultSettlement ??= new AgentResultSettlement
                {
                    State = AgentResultSettlementState.AwaitingResult,
                    TaskRunId = task.Id,
                    WorkId = workId,
                    RunnerId = workerId
                };
            }
            run.Status = WorkflowRunStatus.Running;
            return [new TaskStarted(current.Id, task.Id, workerId)];
        }

        public IReadOnlyList<WorkflowEvent> CompleteTask(DateTimeOffset now, bool advance = true)
        {
            var stage = run.CurrentStage();
            var taskRunId = stage.CurrentTask()?.Id;
            return taskRunId is null ? [] : run.CompleteTask(stage.Id, taskRunId, now, advance);
        }

        public IReadOnlyList<WorkflowEvent> CompleteTask(
            string stageId,
            string taskRunId,
            DateTimeOffset now,
            bool advance = true)
        {
            var match = FindTask(run, stageId, taskRunId);
            if (match is not { } found) return [];
            var (current, task) = found;
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            task.FinishedAt = now;
            task.Interruption = null;
            task.Status = TaskRunStatus.Completed;
            task.TerminalLogOwnership = run.TerminalLogOwnershipFor(task);
            var events = new List<WorkflowEvent>
            {
                new TaskCompleted(current.Id, task.Id)
            };
            if (advance)
                events.AddRange(run.Advance(now));
            return events;
        }

        /// <summary>
        /// Completes a <see cref="TaskRun"/> as Failed and transitions the
        /// <see cref="WorkflowRun"/> to Failed.
        /// This is an <b>event-driven policy reaction</b> of the workflow
        /// aggregate to the task result, not a continuous status derivation
        /// (<c>Status != f(task statuses)</c>). There is no recompute-later
        /// or sync-from-tasks path — the run decides its own transition
        /// on receiving the failure event. This does not violate the
        /// independence of <see cref="WorkflowRunStatus"/> and
        /// <see cref="TaskRunStatus"/> state machines.
        /// </summary>
        public IReadOnlyList<WorkflowEvent> FailTask(TaskResult result, DateTimeOffset now)
        {
            var stage = run.CurrentStage();
            var taskRunId = stage.CurrentTask()?.Id;
            return taskRunId is null ? [] : run.FailTask(stage.Id, taskRunId, result, now);
        }

        public IReadOnlyList<WorkflowEvent> FailTask(
            string stageId,
            string taskRunId,
            TaskResult result,
            DateTimeOffset now)
        {
            var match = FindTask(run, stageId, taskRunId);
            if (match is not { } found) return [];
            var (current, task) = found;
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            task.FinishedAt = now;
            task.Interruption = null;
            task.Status = TaskRunStatus.Failed;
            task.TerminalLogOwnership = run.TerminalLogOwnershipFor(task);
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: result.Reason, Error: result.Error);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new TaskFailed(current.Id, task.Id, result.Reason),
                new StageFailed(current.Id, result.Reason),
                new WorkflowRunFailed(result.Reason)
            ];
        }

        private static (StageRun Stage, TaskRun Task)? FindTask(
            WorkflowRun workflow,
            string stageId,
            string taskRunId)
        {
            var matches = workflow.Stages
                .Where(stage => string.Equals(stage.Id, stageId, StringComparison.Ordinal))
                .SelectMany(stage => stage.Tasks
                    .Where(task => string.Equals(task.Id, taskRunId, StringComparison.Ordinal))
                    .Select(task => (Stage: stage, Task: task)))
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        public IReadOnlyList<WorkflowEvent> FailTaskForStopped(string reason, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            var message = string.IsNullOrWhiteSpace(reason) ? "stopped" : reason;
            task.FinishedAt = now;
            task.Interruption = null;
            task.Status = TaskRunStatus.Failed;
            task.TerminalLogOwnership = run.TerminalLogOwnershipFor(task);
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: message);
            run.Failure = current.Failure;
            return [new TaskFailed(current.Id, task.Id, message)];
        }

        public IReadOnlyList<WorkflowEvent> CancelUnresolvedAgentTaskForStop(DateTimeOffset now)
        {
            var current = run.CurrentStage();
            var task = current.RunningTask;
            if (task is null
                || (task.WorkflowTaskRecovery is null
                    && task.AgentResultSettlement?.State is not
                        (AgentResultSettlementState.Unknown or AgentResultSettlementState.Blocked)))
            {
                return [];
            }

            task.FinishedAt = now;
            task.Interruption = null;
            task.Status = TaskRunStatus.Cancelled;
            task.TerminalLogOwnership = run.TerminalLogOwnershipFor(task);
            return [new TaskCancelled(current.Id, task.Id)];
        }

        /// <summary>
        /// Releases a task whose runtime was confirmed stopped while the run
        /// was paused. Pausing deliberately leaves the current task running so
        /// a normal task report can finish it; a confirmed session stop is the
        /// explicit boundary that makes requeueing safe.
        /// </summary>
        public bool RequeueTaskAfterPausedStop(string workId, string workerId)
        {
            if (run.Status != WorkflowRunStatus.Paused)
                return false;

            var current = run.CurrentStage();
            var task = current.RunningTask;
            var effectiveWorkId = task?.WorkId ?? task?.Id;
            if (task is null
                || !string.Equals(effectiveWorkId, workId, StringComparison.Ordinal)
                || !string.Equals(task.WorkerId, workerId, StringComparison.Ordinal))
            {
                return false;
            }

            task.Status = TaskRunStatus.Pending;
            task.StartedAt = null;
            task.FinishedAt = null;
            task.WorkerId = null;
            task.WorkId = null;
            task.AgentResultSettlement = null;
            task.Output = null;
            task.Error = null;

            if (current.Failure?.TaskId == task.Id)
                current.Failure = null;
            if (run.Failure?.TaskId == task.Id)
                run.Failure = null;
            return true;
        }
    }

    private static bool RequiresAgentResultSettlement(string? uses) =>
        string.Equals(uses, "mohist/agent", StringComparison.Ordinal)
        || string.Equals(uses, "mohist/opencode", StringComparison.Ordinal)
        || string.Equals(uses, "mohist/pi", StringComparison.Ordinal);

    private static TerminalLogOwnership? TerminalLogOwnershipFor(this WorkflowRun run, TaskRun task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkId) || string.IsNullOrWhiteSpace(task.WorkerId))
            return null;

        return new TerminalLogOwnership(
            TerminalLogOwnerKinds.Workflow,
            run.Id,
            task.WorkId,
            task.WorkerId);
    }
}
