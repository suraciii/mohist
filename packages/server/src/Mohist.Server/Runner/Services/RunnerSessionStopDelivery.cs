using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services;

public sealed class RunnerSessionStopDelivery(
    IRunnerControlTransport control,
    ILogger<RunnerSessionStopDelivery> log) : ISessionStopDelivery, IScopedService
{
    public async Task<SessionStopDeliveryResponse> DispatchAsync(
        SessionStopDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Runtime)
            || string.IsNullOrWhiteSpace(request.RuntimeSessionId)
            || string.IsNullOrWhiteSpace(request.WorkDir))
            return new(null, DispatchStarted: false);

        var binding = new RunnerSessionBinding(
            request.Runtime,
            request.RuntimeSessionId,
            request.RunnerId,
            request.WorkDir);
        var target = string.Equals(request.SourceKind, "workflow", StringComparison.Ordinal)
            ? new RunnerSessionTarget(
                "workflow",
                request.ProjectId,
                binding,
                WorkflowRunId: request.WorkflowRunId,
                SessionName: request.SessionName)
            : new RunnerSessionTarget(
                "generic",
                request.ProjectId,
                binding,
                SessionId: request.SessionId);

        var dispatchStarted = false;
        try
        {
            return new(
                await control.SendRequestAsync<SessionStopParams, RunnerStopReply>(
                request.RunnerId,
                "session.stop",
                new SessionStopParams(target, request.SessionId, request.TurnId, request.OperationId),
                () => dispatchStarted = true,
                cancellationToken),
                DispatchStarted: dispatchStarted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Runner {RunnerId} failed to redeliver stop for AgentSession {SessionId} turn {TurnId}",
                request.RunnerId,
                request.SessionId,
                request.TurnId);
            return new(null, DispatchStarted: dispatchStarted);
        }
    }
}
