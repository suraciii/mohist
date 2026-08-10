using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Sessions.Services;

public sealed record SessionStopDeliveryRequest(
    string ProjectId,
    string SessionId,
    string TurnId,
    string OperationId,
    string RunnerId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir);

public interface ISessionStopDelivery
{
    Task<RunnerStopReply?> DispatchAsync(
        SessionStopDeliveryRequest request,
        CancellationToken cancellationToken = default);
}
