using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    public sealed record WorkflowRetryTarget(FailureReason Reason, string Target);

    extension(WorkflowRun run)
    {
        public FailureDetails? EffectiveFailure()
        {
            if (run.Failure is not null) return run.Failure;
            if (run.CurrentStageId is null) return null;
            return run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId)?.Failure;
        }

        public WorkflowRetryTarget? RetryTarget(FailureDetails? failureOverride = null)
        {
            var failure = failureOverride ?? run.EffectiveFailure();
            if (run.Status != WorkflowRunStatus.Failed || failure is null) return null;

            if (failure.Reason is FailureReason.TaskFailed)
            {
                var taskId = failure.TaskId;
                if (taskId is null && failure.Stage is not null)
                {
                    var failedStage = run.Stages.FirstOrDefault(s => s.Id == failure.Stage);
                    taskId = failedStage?.Tasks.LastOrDefault(t => t.Status == TaskRunStatus.Failed)?.Id;
                }

                return taskId is not null
                    ? new WorkflowRetryTarget(FailureReason.TaskFailed, taskId)
                    : null;
            }

            if (failure.Reason is FailureReason.CheckFailed && failure.CheckName is not null)
                return new WorkflowRetryTarget(FailureReason.CheckFailed, failure.CheckName);

            return null;
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

        public IReadOnlyList<WorkflowEvent> Retry(DateTimeOffset now)
        {
            if (run.Status != WorkflowRunStatus.Failed)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, retry requires failed");

            var current = run.CurrentStage();
            if (current.Failure is null)
                throw new InvalidOperationException($"Stage {current.Id} is not failed");

            switch (current.Failure.Reason)
            {
                case FailureReason.TaskFailed:
                {
                    var taskId = current.Failure.TaskId
                        ?? current.Tasks.LastOrDefault(t => t.Status == TaskRunStatus.Failed)?.Id;
                    if (taskId is null)
                        throw new InvalidOperationException($"Stage {current.Id} task failure has no task ID; use rerun to restart the stage");

                    current.RetryFailedTask(taskId);
                    run.Failure = null;
                    ApplyWaitingForDispatchStatus(run, now);
                    return [new WorkflowRunResumed()];
                }
                case FailureReason.CheckFailed:
                    current.RetryFailedCheck(current.Failure.CheckName);
                    run.Failure = null;
                    ApplyWaitingForDispatchStatus(run, now);
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

        public IReadOnlyList<WorkflowEvent> Rerun(DateTimeOffset now)
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
            ApplyWaitingForDispatchStatus(run, now);
            return [
                new WorkflowRunResumed(),
                new StageStarted(newStage.Id)
            ];
        }

        public IReadOnlyList<WorkflowEvent> RerunFromStage(string stageId, DateTimeOffset now)
        {
            var targetIdx = run.Stages.FindIndex(s => s.Id == stageId);
            var currentIdx = run.Stages.FindIndex(s => s.Id == run.CurrentStageId);
            var eligibleStages = run.Stages
                .Take(currentIdx + 1)
                .Select(s => s.Id)
                .ToList();

            if (targetIdx < 0)
                throw new WorkflowControlRejectionException(
                    "unknown_stage",
                    $"Stage '{stageId}' is not part of this workflow run.",
                    new
                    {
                        eligibleStages
                    });

            if (targetIdx > currentIdx)
                throw new WorkflowControlRejectionException(
                    "stage_not_reached",
                    $"Stage '{stageId}' has not been reached yet. Choose a stage the workflow run has already reached.",
                    new
                    {
                        eligibleStages
                    });

            for (var i = targetIdx; i < run.Stages.Count; i++)
            {
                var stage = run.Stages[i];
                if (stage.Tasks.Any(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed)))
                    throw new WorkflowControlRejectionException(
                        "active_work_in_range",
                        $"Cannot rerun from stage '{stageId}' because there is active work in the invalidation range. Stop or cancel the active work first, then retry.");
                if (stage.Checks.Any(c => c.Status == StageCheckStatus.Running))
                    throw new WorkflowControlRejectionException(
                        "active_work_in_range",
                        $"Cannot rerun from stage '{stageId}' because there is active work in the invalidation range. Stop or cancel the active work first, then retry.");
            }

            var target = run.Stages[targetIdx];
            run.Stages[targetIdx] = new StageRun
            {
                Id = target.Id,
                Attempt = target.Attempt + 1,
                RequiresApproval = target.RequiresApproval,
                Status = StageRunStatus.Running,
            };

            for (var i = targetIdx + 1; i < run.Stages.Count; i++)
            {
                var later = run.Stages[i];
                run.Stages[i] = new StageRun
                {
                    Id = later.Id,
                    Attempt = 1,
                    RequiresApproval = later.RequiresApproval,
                    Status = StageRunStatus.Pending,
                };
            }

            run.CurrentStageId = run.Stages[targetIdx].Id;
            run.Failure = null;
            ApplyWaitingForDispatchStatus(run, now);

            return [
                new WorkflowRunResumed(),
                new StageStarted(run.Stages[targetIdx].Id)
            ];
        }

        private void ResetStageFailure()
        {
            var current = run.CurrentStage();
            current.Failure = null;
        }
    }
}
