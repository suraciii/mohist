using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void FailStage(string reason)
        {
            var current = run.CurrentStage();
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, Message: reason);
            run.Failure = current.Failure;
            current.Phase = StageRunPhase.Failed;
            run.Phase = WorkflowRunPhase.Failed;
        }

        public void FailInFlightWork(string workType, string? reason)
        {
            var current = run.CurrentStage();

            switch (workType)
            {
                case "task":
                {
                    var task = current.FirstPendingTask();
                    if (task is null) return;
                    task.Phase = TaskRunPhase.Failed;
                    current.Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, task.Id, Message: reason);
                    run.Failure = current.Failure;
                    current.Phase = StageRunPhase.Failed;
                    run.Phase = WorkflowRunPhase.Failed;
                    break;
                }
                case "check" or "checks":
                {
                    var pending = current.FirstPendingCheck();
                    if (pending is null) return;
                    pending.Phase = CheckRunPhase.Failed;
                    pending.Message = reason;
                    current.Failure = new FailureDetails(FailureReason.CheckUnrepaired, current.StageId, CheckName: pending.Name, Message: reason);
                    run.Failure = current.Failure;
                    current.Phase = StageRunPhase.Failed;
                    run.Phase = WorkflowRunPhase.Failed;
                    break;
                }
                default:
                {
                    current.Failure = new FailureDetails(FailureReason.TaskFailed, current.StageId, Message: reason ?? $"In-flight work lost (type={workType})");
                    run.Failure = current.Failure;
                    current.Phase = StageRunPhase.Failed;
                    run.Phase = WorkflowRunPhase.Failed;
                    break;
                }
            }
        }

        public void Retry()
        {
            if (run.Phase != WorkflowRunPhase.Failed)
                throw new WorkflowDomainException($"WorkflowRun is {run.Phase}, retry requires failed");

            var current = run.CurrentStage();
            if (current.Failure is null)
                throw new WorkflowDomainException($"Stage {current.StageId} is not failed");

            switch (current.Failure.Reason)
            {
                case FailureReason.TaskFailed when current.Failure.TaskId is not null:
                    RetryFailedTask(run, current, current.Failure.TaskId);
                    break;
                case FailureReason.TaskFailed:
                    throw new WorkflowDomainException($"Stage {current.StageId} task failure has no task ID; use rerun to restart the stage");
                case FailureReason.CheckUnrepaired:
                    RetryFailedCheck(run, current, current.Failure.CheckName);
                    break;
                case FailureReason.ApprovalRejected:
                    throw new WorkflowDomainException($"Stage {current.StageId} failure is approval rejection; use rerun to restart the stage");
                default:
                    throw new WorkflowDomainException($"Unknown failure reason: {current.Failure.Reason}");
            }
        }

        public void Rerun()
        {
            var current = run.CurrentStage();
            var stageIdx = run.Stages.FindIndex(s => s.StageId == current.StageId);
            var newStage = new StageRun
            {
                StageId = current.StageId,
                Order = current.Order,
                Attempt = current.Attempt + 1,
                RequiresApproval = current.RequiresApproval,
                Phase = StageRunPhase.Running
            };
            run.Stages[stageIdx] = newStage;
            run.Phase = WorkflowRunPhase.Running;
        }
    }

    private static void RetryFailedTask(WorkflowRun run, StageRun stage, string taskRunId)
    {
        var failedTask = stage.Tasks.LastOrDefault(t => t.Id == taskRunId && t.Phase == TaskRunPhase.Failed)
            ?? throw new WorkflowDomainException($"Failed task {taskRunId} not found or not in failed state");

        var input = new LoadedTaskInput(failedTask.DefinitionId, failedTask.Title, failedTask.Uses, failedTask.WithInput);
        var newTask = TaskRun.MakeTask(stage.Tasks, input);
        stage.Tasks.Add(newTask);
        stage.Failure = null;
        stage.Phase = StageRunPhase.Running;
        run.Phase = WorkflowRunPhase.Running;
    }

    private static void RetryFailedCheck(WorkflowRun run, StageRun stage, string? checkName)
    {
        var failedCheck = stage.Checks.FirstOrDefault(c => c.Name == checkName && c.Phase == CheckRunPhase.Failed)
            ?? throw new WorkflowDomainException($"Failed check {checkName} not found or not in failed state");

        failedCheck.Phase = CheckRunPhase.Pending;
        failedCheck.Message = null;
        failedCheck.Output = null;
        stage.Failure = null;
        stage.Phase = StageRunPhase.Running;
        run.Phase = WorkflowRunPhase.Running;
    }
}
