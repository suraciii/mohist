using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void Approve()
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new WorkflowDomainException($"Stage {current.StageId} is not awaiting approval");

            current.ApprovalStatus = new ApprovalStatus(
                "approved",
                current.ApprovalStatus!.RequestedAt,
                DateTimeOffset.UtcNow.ToString("O"));
            current.Status = StageRunStatus.Completed;
            run.Advance();
        }

        public void Reject(string? reason = null)
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new WorkflowDomainException($"Stage {current.StageId} is not awaiting approval");

            current.ApprovalStatus = new ApprovalStatus(
                "rejected",
                current.ApprovalStatus!.RequestedAt,
                DateTimeOffset.UtcNow.ToString("O"));
            current.Failure = new FailureDetails(FailureReason.ApprovalRejected, current.StageId, Message: reason);
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
        }
    }
}
