using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
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
                    run.WorkspaceMaterializedAt = null;
                    run.Failure = null;
                    run.Status = WorkflowRunStatus.Running;
                    return [new WorkflowRunResumed()];
                case FailureReason.TaskFailed:
                    throw new InvalidOperationException($"Stage {current.Id} task failure has no task ID; use rerun to restart the stage");
                case FailureReason.CheckUnrepaired:
                    current.RetryFailedCheck(current.Failure.CheckName);
                    run.WorkspaceMaterializedAt = null;
                    run.Failure = null;
                    run.Status = WorkflowRunStatus.Running;
                    return [new WorkflowRunResumed()];
                case FailureReason.ContextExhaustion:
                    throw new InvalidOperationException($"Stage {current.Id} failure is context exhaustion; use compact or reset on the session before retrying");
                case FailureReason.ApprovalRejected:
                    throw new InvalidOperationException($"Stage {current.Id} failure is approval rejection; use rerun to restart the stage");
                default:
                    throw new InvalidOperationException($"Unknown failure reason: {current.Failure.Reason}");
            }
        }

        public IReadOnlyList<WorkflowEvent> BlockStageWithContextExhaustion(
            string? taskId,
            double? contextUsagePercent,
            string? sessionId)
        {
            var current = run.CurrentStage();
            var message = contextUsagePercent is null
                ? "Session context is near capacity. Compact or reset the session before retrying."
                : $"Session context is near capacity ({contextUsagePercent:0.##}%). Compact or reset the session before retrying.";
            current.Failure = new FailureDetails(
                FailureReason.ContextExhaustion,
                current.Id,
                TaskId: taskId,
                Message: message);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new StageFailed(current.Id, message),
                new WorkflowRunFailed(message)
            ];
        }

        /// <summary>
        /// Demote a <see cref="FailureReason.ContextExhaustion"/> failure back to
        /// <see cref="FailureReason.TaskFailed"/> so the normal retry path can
        /// proceed. The workflow grain calls this when the user has recovered
        /// the session (via compact/reset) and the gate now reports a healthy
        /// context window. The original TaskId is preserved so the standard
        /// retry handler re-runs the right task.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the failure was rewritten; <c>false</c> when the
        /// current stage had no <see cref="FailureReason.ContextExhaustion"/>
        /// failure to clear.
        /// </returns>
        public bool ClearContextExhaustionFailure()
        {
            var current = run.CurrentStage();
            var failure = current.Failure;
            if (failure is null || failure.Reason != FailureReason.ContextExhaustion)
            {
                return false;
            }

            var demoted = new FailureDetails(
                FailureReason.TaskFailed,
                failure.Stage,
                TaskId: failure.TaskId,
                CheckName: failure.CheckName,
                Message: failure.Message);
            current.Failure = demoted;
            run.Failure = demoted;
            return true;
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
            run.WorkspaceMaterializedAt = null;
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
