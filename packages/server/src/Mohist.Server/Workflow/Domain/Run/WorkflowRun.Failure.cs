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

        /// <summary>
        /// Non-mutating evaluation: returns true when the current stage is
        /// failed and represents a retryable failure that <see cref="Retry"/>
        /// could clear. Returns false when the run is not failed, the
        /// failure is on a different stage, or the current stage is in an
        /// active approval feedback loop.
        /// </summary>
        public bool IsCurrentStageRetryableFailure()
        {
            if (run.Status != WorkflowRunStatus.Failed) return false;

            var current = run.CurrentStage();
            if (current.IsFeedbackLoop) return false;

            return current.Failure is not null;
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
