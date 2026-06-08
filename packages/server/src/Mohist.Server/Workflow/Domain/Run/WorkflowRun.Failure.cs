using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> FailStage(string reason)
        {
            var current = run.CurrentStage();
            current.Failure = new FailureDetails(FailureReason.TaskFailed, current.Id, Message: reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new StageFailed(current.Id, reason),
                new WorkflowRunFailed(reason)
            ];
        }

        public IReadOnlyList<WorkflowEvent> Retry()
        {
            if (run.Status != WorkflowRunStatus.Failed)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, retry requires failed");

            var current = run.CurrentStage();
            if (current.Failure is null)
                throw new InvalidOperationException($"Stage {current.Id} is not failed");

            switch (current.Failure.Reason)
            {
                case FailureReason.TaskFailed when current.Failure.TaskId is not null:
                    current.RetryFailedTask(current.Failure.TaskId);
                    run.Failure = null;
                    run.Status = WorkflowRunStatus.Running;
                    return [new WorkflowRunResumed()];
                case FailureReason.TaskFailed:
                    throw new InvalidOperationException($"Stage {current.Id} task failure has no task ID; use rerun to restart the stage");
                case FailureReason.CheckUnrepaired:
                    current.RetryFailedCheck(current.Failure.CheckName);
                    run.Failure = null;
                    run.Status = WorkflowRunStatus.Running;
                    return [new WorkflowRunResumed()];
                case FailureReason.ApprovalRejected:
                    throw new InvalidOperationException($"Stage {current.Id} failure is approval rejection; use rerun to restart the stage");
                default:
                    throw new InvalidOperationException($"Unknown failure reason: {current.Failure.Reason}");
            }
        }

        public IReadOnlyList<WorkflowEvent> Rerun()
        {
            var current = run.CurrentStage();
            var stageIdx = run.Stages.FindIndex(s => s.Id == current.Id);
            var newStage = new StageRun
            {
                Id = current.Id,
                Attempt = current.Attempt + 1,
                RequiresApproval = current.RequiresApproval,
                Status = StageRunStatus.Running,
                // Carry the rejection reason over to the new stage so the
                // operator can see "why was this rejected last time"
                // while the rerun is in flight. Cleared on the next
                // successful Approve (or overwritten on a fresh Reject).
                LastRejectionReason = current.LastRejectionReason,
            };
            run.Stages[stageIdx] = newStage;
            run.Failure = null;
            run.Status = WorkflowRunStatus.Running;
            return [
                new WorkflowRunResumed(),
                new StageStarted(newStage.Id)
            ];
        }

        private void ResetStageFailure()
        {
            var current = run.CurrentStage();
            current.Failure = null;
        }
    }
}
