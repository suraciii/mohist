using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

public sealed class WorkflowDefinitionValidationException : InvalidOperationException
{
    public WorkflowDefinitionValidationException(IReadOnlyList<ValidationError> errors)
        : base("Workflow Definition is invalid")
    {
        Errors = errors;
    }

    public IReadOnlyList<ValidationError> Errors { get; }
}
