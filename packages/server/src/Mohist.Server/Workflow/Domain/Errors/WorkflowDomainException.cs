namespace Mohist.Server.Workflow.Domain.Errors;

public class WorkflowDomainException : Exception
{
    public WorkflowDomainException(string message) : base(message) { }
}
