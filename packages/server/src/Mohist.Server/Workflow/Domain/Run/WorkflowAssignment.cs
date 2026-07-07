using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Represents one worker's exclusive assignment on a <see cref="WorkflowRun"/>.
/// A running task's worker id is derived from this assignment and is used only
/// to reject stale reports from a different worker.
/// </summary>
public sealed record WorkflowAssignment(string WorkerId, DateTimeOffset AssignedAt);

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public void AssignTo(string workerId, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(workerId))
                throw new InvalidOperationException("Worker id is required");

            if (run.Assignment is not null)
                throw new InvalidOperationException($"Workflow is already assigned to {run.Assignment.WorkerId}");

            if (run.Status != WorkflowRunStatus.Pending)
                throw new InvalidOperationException($"Workflow is {run.Status}, assignment requires Pending");

            if (!run.HasDispatchableWork())
                throw new InvalidOperationException("Workflow has no dispatchable work");

            run.Assignment = new WorkflowAssignment(workerId, now);
            run.Status = WorkflowRunStatus.Ready;
            run.ReadySince ??= now;
        }

        public bool IsAssignedTo(string workerId)
            => run.Assignment is not null && string.Equals(run.Assignment.WorkerId, workerId, StringComparison.Ordinal);

        public void RequireAssignedTo(string workerId)
        {
            if (!run.IsAssignedTo(workerId))
                throw new InvalidOperationException(run.Assignment is null
                    ? "Workflow is not assigned"
                    : $"Workflow is assigned to {run.Assignment.WorkerId}, not {workerId}");
        }
    }
}
