using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Represents a runner's exclusive claim on a <see cref="WorkflowRun"/>.
/// The single-runner invariant guarantees at most one claim exists for the
/// entire lifecycle of a run. Once this record is set on
/// <see cref="WorkflowRun.Claim"/>, the <c>RunnerId</c> is the unique
/// runner identity for that run. A <c>Running</c> <see cref="TaskRun"/>'s
/// <c>RunnerId</c> equals <c>Claim.RunnerId</c> as a derivation of this
/// invariant, not as a separately synchronized fact.
/// </summary>
public sealed record WorkflowClaimInfo(string RunnerId, DateTimeOffset ClaimedAt);

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void ClaimBy(string runnerId, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(runnerId))
                throw new InvalidOperationException("Runner id is required");

            if (run.Claim is not null)
                throw new InvalidOperationException($"Workflow is already claimed by {run.Claim.RunnerId}");

            run.Claim = new WorkflowClaimInfo(runnerId, now);
        }

        public bool IsClaimedBy(string runnerId)
            => run.Claim is not null && string.Equals(run.Claim.RunnerId, runnerId, StringComparison.Ordinal);

        public void RequireClaimedBy(string runnerId)
        {
            if (!run.IsClaimedBy(runnerId))
                throw new InvalidOperationException(run.Claim is null
                    ? "Workflow is not claimed"
                    : $"Workflow is claimed by {run.Claim.RunnerId}, not {runnerId}");
        }

        public void ReleaseClaim()
        {
            run.Claim = null;
        }
    }
}
