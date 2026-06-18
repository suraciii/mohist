using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> StartTask(string workId, string runnerId)
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null) return [];

            task.Status = TaskRunStatus.Running;
            task.StartedAt = DateTimeOffset.UtcNow;
            task.WorkId = workId;
            task.RunnerId = runnerId;
            return [new TaskStarted(current.Id, task.Id, runnerId)];
        }

        public IReadOnlyList<WorkflowEvent> CompleteTask()
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            task.FinishedAt = DateTimeOffset.UtcNow;
            task.Status = TaskRunStatus.Completed;
            var events = new List<WorkflowEvent>
            {
                new TaskCompleted(current.Id, task.Id)
            };
            events.AddRange(run.Advance());
            return events;
        }

        public IReadOnlyList<WorkflowEvent> FailTask(TaskResult result)
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            task.FinishedAt = DateTimeOffset.UtcNow;
            task.Status = TaskRunStatus.Failed;
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: result.Reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new TaskFailed(current.Id, task.Id, result.Reason),
                new StageFailed(current.Id, result.Reason),
                new WorkflowRunFailed(result.Reason)
            ];
        }

        public IReadOnlyList<WorkflowEvent> FailTaskForRunnerLost()
            => run.FailTask(new TaskResult("failed", "runner-lost"));

        public IReadOnlyList<WorkflowEvent> FailTaskForStopped(string reason)
        {
            var current = run.CurrentStage();
            var task = current.CurrentTask();
            if (task is null || task.Status != TaskRunStatus.Running) return [];

            var message = string.IsNullOrWhiteSpace(reason) ? "stopped" : reason;
            task.FinishedAt = DateTimeOffset.UtcNow;
            task.Status = TaskRunStatus.Failed;
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: message);
            run.Failure = current.Failure;
            return [new TaskFailed(current.Id, task.Id, message)];
        }
    }
}
