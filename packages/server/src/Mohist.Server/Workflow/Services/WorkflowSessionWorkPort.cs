using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Services;

public sealed class WorkflowSessionWorkPort(IGrainFactory grains) : ISessionWorkPort, IScopedService
{
    public Task AbandonActiveWorkAsync(
        SessionWorkflowWorkBinding binding,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return grains
            .GetGrain<IWorkflowGrain>(binding.WorkflowRunId)
            .AbandonActiveWorkAsync(binding.RunnerId, binding.WorkId, reason);
    }
}
