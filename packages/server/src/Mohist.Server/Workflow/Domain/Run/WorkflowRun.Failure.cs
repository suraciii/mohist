using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    public sealed record WorkflowRetryTarget(FailureReason Reason, string Target);

    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> FailDefinitionResolution(string message)
        {
            var current = run.CurrentStage();
            current.Failure = new FailureDetails(
                FailureReason.DefinitionResolutionFailed,
                current.Id,
                Message: message);
            run.Failure = current.Failure;
            run.Status = WorkflowRunStatus.Failed;
            return [new WorkflowRunFailed(message)];
        }

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

            if (failure.Reason is FailureReason.TaskFailed or FailureReason.ContextExhaustion)
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

            var target = run.RetryTarget();
            if (target is null)
                throw new InvalidOperationException($"Stage {current.Id} failure has no retry target; use rerun to restart the stage");

            switch (target.Reason)
            {
                case FailureReason.TaskFailed:
                    current.RetryFailedTask(run, target.Target);
                    run.Failure = null;
                    ApplyWaitingForDispatchStatus(run, now);
                    return [new WorkflowRunResumed()];
                case FailureReason.CheckFailed:
                    current.RetryFailedCheck(target.Target);
                    run.Failure = null;
                    ApplyWaitingForDispatchStatus(run, now);
                    return [new WorkflowRunResumed()];
                default:
                    throw new InvalidOperationException($"Stage {current.Id} failure cannot be retried; use rerun to restart the stage");
            }
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
                if (stage.Tasks.Any(t => t.Status == TaskRunStatus.Running))
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
                    Attempt = later.Initialized ? later.Attempt + 1 : later.Attempt,
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

    }
}
