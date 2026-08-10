using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services.SignalR;

public sealed class RunnerSessionStopDelivery(
    IHubContext<RunnerHub> runnerHub,
    RunnerConnectionTracker connections,
    ILogger<RunnerSessionStopDelivery> log) : ISessionStopDelivery, IScopedService
{
    public async Task<SessionStopDeliveryResponse> DispatchAsync(
        SessionStopDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionId = connections.GetConnectionId(request.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId)
            || string.IsNullOrWhiteSpace(request.Runtime)
            || string.IsNullOrWhiteSpace(request.RuntimeSessionId)
            || string.IsNullOrWhiteSpace(request.WorkDir))
            return new(null, DispatchStarted: false);

        object binding = new
        {
            runtime = request.Runtime,
            runtimeSessionId = request.RuntimeSessionId,
            runnerId = request.RunnerId,
            workDir = request.WorkDir,
        };
        object target = string.Equals(request.SourceKind, "workflow", StringComparison.Ordinal)
            ? new
            {
                kind = "workflow",
                projectId = request.ProjectId,
                workflowRunId = request.WorkflowRunId,
                sessionName = request.SessionName,
                binding,
            }
            : new
            {
                kind = "generic",
                projectId = request.ProjectId,
                sessionId = request.SessionId,
                binding,
            };

        try
        {
            return new(
                await runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerStopReply?>(
                "CancelAgentSession",
                new
                {
                    target,
                    sessionId = request.SessionId,
                    turnId = request.TurnId,
                    operationId = request.OperationId,
                },
                cancellationToken),
                DispatchStarted: true);
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
            return new(null, DispatchStarted: true);
        }
    }
}
