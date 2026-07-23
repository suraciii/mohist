using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Thrown when the Workflow control plane can render a dispatch envelope
/// for an active work item. Carries the structured <see cref="ExecutionError"/>
/// the owning <see cref="Mohist.Server.Workflow.Grains.IWorkflowGrain"/>
/// persists on the rejected <see cref="Mohist.Server.Workflow.Domain.Run.TaskRun"/>;
/// recovery handlers match <c>failure.error.code</c> against this code.
/// </summary>
internal sealed class WorkflowDispatchRejectedException : Exception
{
    public WorkflowDispatchRejectedException(string message, ExecutionError error)
        : base(message)
    {
        Error = error;
    }

    public ExecutionError Error { get; }
}
