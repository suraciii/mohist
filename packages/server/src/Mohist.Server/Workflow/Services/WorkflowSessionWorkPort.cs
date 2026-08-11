using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Services;

public sealed class WorkflowSessionWorkPort(IGrainFactory grains) : ISessionWorkPort, IScopedService
{
    public async Task<bool> BindAgentExecutionAsync(
        SessionWorkflowExecutionBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ack = await grains
            .GetGrain<IWorkflowGrain>(binding.WorkflowRunId)
            .BindAgentExecutionAsync(new AgentExecutionBinding(
                binding.TaskRunId,
                binding.WorkId,
                binding.RunnerId,
                binding.AgentSessionId,
                binding.AgentTurnId,
                binding.Runtime,
                binding.RuntimeSessionId));
        return ack == ReportAck.Accepted;
    }

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
