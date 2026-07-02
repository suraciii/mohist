using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Represents a runner's exclusive assignment on a <see cref="WorkflowRun"/>.
/// The single-runner invariant guarantees at most one assignment exists for the
/// entire lifecycle of a run. Once this record is set on
/// <see cref="WorkflowRun.Assignment"/>, the <c>RunnerId</c> is the unique
/// runner identity for that run. A <c>Running</c> <see cref="TaskRun"/>'s
/// <c>RunnerId</c> equals <c>Assignment.RunnerId</c> as a derivation of this
/// invariant, not as a separately synchronized fact.
/// </summary>
public sealed record WorkflowAssignment(string RunnerId, DateTimeOffset AssignedAt);

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void AssignTo(string runnerId, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(runnerId))
                throw new InvalidOperationException("Runner id is required");

            if (run.Assignment is not null)
                throw new InvalidOperationException($"Workflow is already assigned to {run.Assignment.RunnerId}");

            if (run.Status != WorkflowRunStatus.Pending)
                throw new InvalidOperationException($"Workflow is {run.Status}, assignment requires Pending");

            if (!run.HasDispatchableWork())
                throw new InvalidOperationException("Workflow has no dispatchable work");

            run.Assignment = new WorkflowAssignment(runnerId, now);
            run.Status = WorkflowRunStatus.Ready;
        }

        public bool IsAssignedTo(string runnerId)
            => run.Assignment is not null && string.Equals(run.Assignment.RunnerId, runnerId, StringComparison.Ordinal);

        public void RequireAssignedTo(string runnerId)
        {
            if (!run.IsAssignedTo(runnerId))
                throw new InvalidOperationException(run.Assignment is null
                    ? "Workflow is not assigned"
                    : $"Workflow is assigned to {run.Assignment.RunnerId}, not {runnerId}");
        }
    }
}
