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
    }

    extension(StageRun stage)
    {
        internal TaskRun? FirstPendingTask()
            => stage.Tasks.FirstOrDefault(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));

        internal StageCheck FindCheck(string name)
            => stage.Checks.FirstOrDefault(c => c.Name == name)
                ?? throw new WorkflowDomainException($"Check {name} not found in stage {stage.StageId}");

        internal StageCheck FirstPendingCheck()
            => stage.Checks.FirstOrDefault(c => c.Status == StageCheckStatus.Pending)!;

        internal bool IsComplete()
        {
            if (!stage.Initialized) return false;
            var hasPendingTask = stage.Tasks.Any(t => t.Status is not (TaskRunStatus.Completed or TaskRunStatus.Failed));
            if (hasPendingTask) return false;
            return stage.Checks.All(c => c.Status == StageCheckStatus.Passed);
        }

        internal void TryRequestApproval()
        {
            if (stage.RequiresApproval && stage.Approval is null && stage.IsComplete())
            {
                stage.Approval = new ApprovalStatus("awaiting", DateTimeOffset.UtcNow.ToString("O"), null);
                stage.Status = StageRunStatus.AwaitingApproval;
                return;
            }
            if (stage.Failure is not null)
            {
                stage.Status = StageRunStatus.Failed;
                return;
            }
            if (stage.Approval?.Status == "awaiting")
            {
                stage.Status = StageRunStatus.AwaitingApproval;
                return;
            }
            if (stage.IsComplete())
            {
                if (stage.RequiresApproval && stage.Approval?.Status != "approved")
                {
                    stage.Status = StageRunStatus.Running;
                    return;
                }
                stage.Status = StageRunStatus.Completed;
                return;
            }
            stage.Status = StageRunStatus.Running;
        }
    }
}
