using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public sealed record WorkflowClaimInfo(string RunnerId, DateTimeOffset ClaimedAt);

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void ClaimBy(string runnerId, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(runnerId))
                throw new WorkflowDomainException("Runner id is required");

            if (run.Claim is not null)
                throw new WorkflowDomainException($"Workflow is already claimed by {run.Claim.RunnerId}");

            run.Claim = new WorkflowClaimInfo(runnerId, now);
        }

        public bool IsClaimedBy(string runnerId)
            => run.Claim is not null && string.Equals(run.Claim.RunnerId, runnerId, StringComparison.Ordinal);

        public void RequireClaimedBy(string runnerId)
        {
            if (!run.IsClaimedBy(runnerId))
                throw new WorkflowDomainException(run.Claim is null
                    ? "Workflow is not claimed"
                    : $"Workflow is claimed by {run.Claim.RunnerId}, not {runnerId}");
        }

        public void ReleaseClaim()
        {
            run.Claim = null;
        }
    }
}
