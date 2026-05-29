using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        internal StageRun CurrentStage()
        {
            if (run.CurrentStageId is null)
                throw new WorkflowDomainException("WorkflowRun has no current stage");
            return run.Stages.FirstOrDefault(s => s.StageId == run.CurrentStageId)
                ?? throw new WorkflowDomainException($"Current stage {run.CurrentStageId} not found");
        }

        internal void RecomputePhase()
        {
            if (run.Phase is WorkflowRunPhase.Pending or WorkflowRunPhase.Paused) return;

            var current = run.CurrentStage();

            if (current.Phase == StageRunPhase.Failed)
            {
                run.Phase = WorkflowRunPhase.Failed;
                return;
            }
            if (current.Phase == StageRunPhase.AwaitingApproval)
            {
                run.Phase = WorkflowRunPhase.AwaitingApproval;
                return;
            }
            if (current.Phase == StageRunPhase.Completed && run.Stages.Count > 0 && run.Stages[^1].StageId == current.StageId)
            {
                run.Phase = WorkflowRunPhase.Completed;
                run.CompletedAt = DateTimeOffset.UtcNow;
                return;
            }
            run.Phase = WorkflowRunPhase.Running;
        }
    }

    extension(StageRun stage)
    {
        internal TaskRun? FirstPendingTask()
            => stage.Tasks.FirstOrDefault(t => t.Phase is not (TaskRunPhase.Completed or TaskRunPhase.Failed));

        internal StageCheck FindCheck(string name)
            => stage.Checks.FirstOrDefault(c => c.Name == name)
                ?? throw new WorkflowDomainException($"Check {name} not found in stage {stage.StageId}");

        internal StageCheck FirstPendingCheck()
            => stage.Checks.FirstOrDefault(c => c.Phase == CheckRunPhase.Pending)!;

        internal bool IsComplete()
        {
            if (!stage.Initialized) return false;
            var hasPendingTask = stage.Tasks.Any(t => t.Phase is not (TaskRunPhase.Completed or TaskRunPhase.Failed));
            if (hasPendingTask) return false;
            return stage.Checks.All(c => c.Phase == CheckRunPhase.Passed);
        }

        internal void TryRequestApproval()
        {
            if (stage.RequiresApproval && stage.Approval is null && stage.IsComplete())
            {
                stage.Approval = new ApprovalState("awaiting", null, DateTimeOffset.UtcNow.ToString("O"), null);
                stage.Phase = StageRunPhase.AwaitingApproval;
                return;
            }
            stage.ComputePhase();
        }

        internal void ComputePhase()
        {
            if (stage.Failure is not null)
            {
                stage.Phase = StageRunPhase.Failed;
                return;
            }
            if (stage.Approval?.Status == "awaiting")
            {
                stage.Phase = StageRunPhase.AwaitingApproval;
                return;
            }
            if (stage.IsComplete())
            {
                if (stage.RequiresApproval && stage.Approval?.Status != "approved")
                {
                    stage.Phase = StageRunPhase.Running;
                    return;
                }
                stage.Phase = StageRunPhase.Completed;
                return;
            }
            stage.Phase = StageRunPhase.Running;
        }
    }
}
