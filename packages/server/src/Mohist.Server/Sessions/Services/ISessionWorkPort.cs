using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public interface ISessionWorkPort
{
    Task<bool> BindAgentExecutionAsync(
        SessionWorkflowExecutionBinding binding,
        CancellationToken cancellationToken = default);

    Task<bool> CanStartAgentCleanupAsync(
        SessionWorkflowExecutionBinding binding,
        CancellationToken cancellationToken = default);

    Task ObserveAgentExecutionAsync(
        SessionWorkflowExecutionBinding binding,
        SessionWorkflowObservationKind kind,
        string reasonCode,
        string? message = null,
        string? stopOperationId = null,
        CancellationToken cancellationToken = default);
}
