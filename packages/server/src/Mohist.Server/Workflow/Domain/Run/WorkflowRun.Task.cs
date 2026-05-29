using System.Text.Json;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public sealed record TaskResult(
    string Status,
    string? Reason = null);

public sealed record LoadedTaskInput(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void CompleteTask()
        {
            var current = run.CurrentStage();
            var task = current.FirstPendingTask();
            if (task is null) return;

            task.Phase = TaskRunPhase.Completed;
            current.TryRequestApproval();
            run.Advance();
        }

        public void FailTask(TaskResult result)
        {
            var current = run.CurrentStage();
            var task = current.FirstPendingTask();
            if (task is null) return;

            task.Phase = TaskRunPhase.Failed;
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, task.Id, Message: result.Reason);
            run.Failure = current.Failure;
            current.Phase = StageRunPhase.Failed;
            run.Phase = WorkflowRunPhase.Failed;
        }

        public void AddRuntimeTask(
            LoadedTaskInput task,
            string? stage = null,
            bool invalidateChecks = false)
        {
            var current = run.CurrentStage();
            if (!string.IsNullOrWhiteSpace(stage) && stage != current.StageId)
                throw new WorkflowDomainException("Cannot add runtime task to stage " + stage + "; current stage is " + current.StageId);

            var newTask = TaskRun.MakeTask(current.Tasks, task);
            current.Tasks.Add(newTask);

            if (invalidateChecks)
            {
                foreach (var c in current.Checks)
                {
                    c.Phase = CheckRunPhase.Pending;
                    c.Message = null;
                    c.Output = null;
                }
            }

            current.Failure = null;
            if (current.Approval?.Status == "awaiting")
                current.Approval = null;
            if (current.Initialized)
                current.Phase = StageRunPhase.Running;

            run.Phase = WorkflowRunPhase.Running;
        }

        public void InsertRuntimeTasksAfter(
            TaskRun afterTask,
            IReadOnlyList<LoadedTaskInput> tasks,
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
                    c.Phase = CheckRunPhase.Pending;
                    c.Message = null;
                    c.Output = null;
                }
            }
        }
    }
}
