using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

public interface IWorkflowRunDeserializer
{
    WorkflowRun? Deserialize(string state);
}

public sealed class WorkflowRunDeserializer : IWorkflowRunDeserializer, ISingletonService
{
    public WorkflowRun? Deserialize(string state) => JSON.Deserialize<WorkflowRun>(state);
}
