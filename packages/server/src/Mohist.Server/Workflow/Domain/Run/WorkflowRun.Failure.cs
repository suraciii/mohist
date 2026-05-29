using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void FailStage(string reason)
        {
            var current = run.CurrentStage();
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, Message: reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
        }

        public void FailCurrentWork(string workType, string? reason)
        {
            var current = run.CurrentStage();

            switch (workType)
            {
                case "task":
                {
                    var task = current.FirstPendingTask();
                    if (task is null) return;
                    task.Status = TaskRunStatus.Failed;
                    current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, task.Id, Message: reason);
                    run.Failure = current.Failure;
                    current.Status = StageRunStatus.Failed;
                    run.Status = WorkflowRunStatus.Failed;
                    break;
                }
                case "check" or "checks":
                {
                    var pending = current.FirstPendingCheck();
                    if (pending is null) return;
                    pending.Status = StageCheckStatus.Failed;
                    pending.Message = reason;
                    current.Failure = new FailureDetails(FailureReason.CheckUnrepaired, current.Id, CheckName: pending.Name, Message: reason);
                    run.Failure = current.Failure;
                    current.Status = StageRunStatus.Failed;
                    run.Status = WorkflowRunStatus.Failed;
                    break;
                }
                default:
                {
                    current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, Message: reason ?? $"In-flight work lost (type={workType})");
                    run.Failure = current.Failure;
                    current.Status = StageRunStatus.Failed;
                    run.Status = WorkflowRunStatus.Failed;
                    break;
                }
            }
        }

        public void Retry()
        {
            if (run.Status != WorkflowRunStatus.Failed)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}, retry requires failed");

            var current = run.CurrentStage();
            if (current.Failure is null)
                throw new WorkflowDomainException($"Stage {current.Id} is not failed");

            switch (current.Failure.Reason)
            {
                case FailureReason.TaskFailed when current.Failure.TaskId is not null:
                    current.RetryFailedTask(current.Failure.TaskId);
                    run.Status = WorkflowRunStatus.Running;
                    break;
                case FailureReason.TaskFailed:
                    throw new WorkflowDomainException($"Stage {current.Id} task failure has no task ID; use rerun to restart the stage");
                case FailureReason.CheckUnrepaired:
                    current.RetryFailedCheck(current.Failure.CheckName);
                    run.Status = WorkflowRunStatus.Running;
                    break;
                case FailureReason.ApprovalRejected:
                    throw new WorkflowDomainException($"Stage {current.Id} failure is approval rejection; use rerun to restart the stage");
                default:
                    throw new WorkflowDomainException($"Unknown failure reason: {current.Failure.Reason}");
            }
        }

        public void Rerun()
        {
            var current = run.CurrentStage();
            var stageIdx = run.Stages.FindIndex(s => s.Id == current.Id);
            var newStage = new StageRun
            {
                Id = current.Id,
                Attempt = current.Attempt + 1,
                RequiresApproval = current.RequiresApproval,
                Status = StageRunStatus.Running
            };
            run.Stages[stageIdx] = newStage;
            run.Status = WorkflowRunStatus.Running;
        }

        public void ResetStageFailure()
        {
            var current = run.CurrentStage();
            current.Failure = null;
        }
    }
}
