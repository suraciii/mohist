using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> Approve()
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            var stageId = current.Id;
            current.ApprovalStatus = new ApprovalStatus(
                "approved",
                current.ApprovalStatus!.RequestedAt,
                DateTimeOffset.UtcNow.ToString("O"));
            current.Status = StageRunStatus.Completed;
            var events = new List<WorkflowEvent>
            {
                new StageApprovalResolved(stageId, ApprovalResult.Approved)
            };
            events.AddRange(run.Advance());
            return events;
        }

        public IReadOnlyList<WorkflowEvent> Reject(string? reason = null)
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            current.ApprovalStatus = new ApprovalStatus(
                "rejected",
                current.ApprovalStatus!.RequestedAt,
                DateTimeOffset.UtcNow.ToString("O"));
            current.Failure = new FailureDetails(FailureReason.ApprovalRejected, current.Id, Message: reason);
            current.LastRejectionReason = reason;
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new StageApprovalResolved(current.Id, ApprovalResult.Rejected, reason),
                new StageFailed(current.Id, reason),
                new WorkflowRunFailed(reason)
            ];
        }
    }
}
