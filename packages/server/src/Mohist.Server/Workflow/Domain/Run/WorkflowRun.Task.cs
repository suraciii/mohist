using Mohist.Server.Workflow.Domain;
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
            run.Status = WorkflowRunStatus.Running;
            return [new TaskStarted(current.Id, task.Id, workerId)];
        }

        public IReadOnlyList<WorkflowEvent> CompleteTask(DateTimeOffset now, bool advance = true)
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            task.FinishedAt = now;
            task.Status = TaskRunStatus.Completed;
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
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            task.FinishedAt = now;
            task.Status = TaskRunStatus.Failed;
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

        public IReadOnlyList<WorkflowEvent> FailTaskForStopped(string reason, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            var message = string.IsNullOrWhiteSpace(reason) ? "stopped" : reason;
            task.FinishedAt = now;
            task.Status = TaskRunStatus.Failed;
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: message);
            run.Failure = current.Failure;
            return [new TaskFailed(current.Id, task.Id, message)];
        }
    }
}
