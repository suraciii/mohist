using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void CompleteTask()
        {
            var current = run.CurrentStage();
            var task = current.FirstPendingTask();
            if (task is null) return;

            task.Status = TaskRunStatus.Completed;
            run.Advance();
        }

        public void FailTask(TaskResult result)
        {
            var current = run.CurrentStage();
            var task = current.FirstPendingTask();
            if (task is null) return;

            task.Status = TaskRunStatus.Failed;
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: result.Reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
        }
    }
}
