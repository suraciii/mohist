using Orleans;

namespace Mohist.Server.Workflow.Domain;

[GenerateSerializer]
public class WorkflowDomainException : Exception
{
    public WorkflowDomainException(string message) : base(message) { }
}
