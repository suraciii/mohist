using System.Text.Json;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public sealed record ApprovalInput(JsonElement? Output = null);

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void Approve(ApprovalInput? input = null)
        {
            var current = run.CurrentStage();
            if (current.Approval?.Status != "awaiting")
                throw new WorkflowDomainException($"Stage {current.StageId} is not awaiting approval");

            current.Approval = new ApprovalState(
                "approved",
                input?.Output ?? null,
                current.Approval.RequestedAt,
                DateTimeOffset.UtcNow.ToString("O"));
            current.Status = StageRunStatus.Completed;
            run.Advance();
        }

        public void Reject(ApprovalInput? input = null)
        {
            var current = run.CurrentStage();
            if (current.Approval?.Status != "awaiting")
                throw new WorkflowDomainException($"Stage {current.StageId} is not awaiting approval");

            var message = input?.Output?.GetString();
            current.Approval = new ApprovalState(
                "rejected",
                input?.Output ?? null,
                current.Approval.RequestedAt,
                DateTimeOffset.UtcNow.ToString("O"));
            current.Failure = new FailureDetails(FailureReason.ApprovalRejected, current.StageId, Message: message);
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
        }
    }
}
