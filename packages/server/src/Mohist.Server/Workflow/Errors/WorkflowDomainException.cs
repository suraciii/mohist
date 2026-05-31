using Orleans;

namespace Mohist.Server.Workflow.Errors;

[GenerateSerializer]
public class WorkflowDomainException : Exception
{
    public WorkflowDomainException(string message) : base(message) { }
}
