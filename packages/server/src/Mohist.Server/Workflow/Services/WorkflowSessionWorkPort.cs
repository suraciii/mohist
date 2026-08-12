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

    public async Task ObserveAgentExecutionAsync(
        SessionWorkflowExecutionBinding binding,
        SessionWorkflowObservationKind kind,
        string reasonCode,
        string? message = null,
        string? stopOperationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await grains
            .GetGrain<IWorkflowGrain>(binding.WorkflowRunId)
            .ObserveAgentExecutionAsync(new AgentExecutionObservation(
                new AgentExecutionBinding(
                    binding.TaskRunId,
                    binding.WorkId,
                    binding.RunnerId,
                    binding.AgentSessionId,
                    binding.AgentTurnId,
                    binding.Runtime,
                    binding.RuntimeSessionId),
                ToWorkflowObservationKind(kind),
                reasonCode,
                message,
                stopOperationId));
    }

    private static AgentExecutionObservationKind ToWorkflowObservationKind(SessionWorkflowObservationKind kind) => kind switch
    {
        SessionWorkflowObservationKind.Idle => AgentExecutionObservationKind.Idle,
        SessionWorkflowObservationKind.Completed => AgentExecutionObservationKind.Completed,
        SessionWorkflowObservationKind.Failed => AgentExecutionObservationKind.Failed,
        SessionWorkflowObservationKind.Cancelled => AgentExecutionObservationKind.Cancelled,
        SessionWorkflowObservationKind.Unknown => AgentExecutionObservationKind.Unknown,
        SessionWorkflowObservationKind.Stopped => AgentExecutionObservationKind.Stopped,
        SessionWorkflowObservationKind.StopUnconfirmed => AgentExecutionObservationKind.StopUnconfirmed,
        SessionWorkflowObservationKind.TargetMissing => AgentExecutionObservationKind.TargetMissing,
        SessionWorkflowObservationKind.Disconnected => AgentExecutionObservationKind.Disconnected,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Session Workflow observation."),
    };
}
