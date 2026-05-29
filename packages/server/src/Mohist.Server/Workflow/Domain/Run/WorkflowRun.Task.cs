using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public sealed record TaskResult(
    string Status,
    string? Reason = null);

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
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, task.Id, Message: result.Reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
        }

        public void AddRuntimeTask(
            TaskDefinition task,
            string? stage = null,
            bool invalidateChecks = false)
        {
            var current = run.CurrentStage();
            if (!current.Initialized)
                throw new WorkflowDomainException($"Cannot add runtime task: stage {current.StageId} is not initialized");
            if (!string.IsNullOrWhiteSpace(stage) && stage != current.StageId)
                throw new WorkflowDomainException("Cannot add runtime task to stage " + stage + "; current stage is " + current.StageId);

            var newTask = TaskRun.MakeTask(current.Tasks, task);
            current.Tasks.Add(newTask);

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
            if (current.Approval?.Status == "awaiting")
                current.Approval = null;
            if (current.Initialized)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
        }

        public void InsertRuntimeTasksAfter(
            TaskRun afterTask,
            IReadOnlyList<TaskDefinition> tasks,
            bool invalidateChecks = false)
        {
            var current = run.CurrentStage();
            var insertIndex = current.Tasks.IndexOf(afterTask) + 1;
            if (insertIndex <= 0) insertIndex = current.Tasks.Count;

            foreach (var task in tasks)
            {
                var newTask = TaskRun.MakeTask(current.Tasks, task);
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
        }
    }
}
