using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionFollowupDispatcher : IScopedService
{
    private readonly AgentSessionQuerier _sessions;
    private readonly IGrainFactory _grains;
    private readonly IHubContext<RunnerHub> _runnerHub;
    private readonly RunnerConnectionTracker _connections;

    public AgentSessionFollowupDispatcher(
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections)
    {
        _sessions = sessions;
        _grains = grains;
        _runnerHub = runnerHub;
        _connections = connections;
    }

    public async Task DispatchNextAsync(string projectId, string sessionId, CancellationToken ct)
    {
        var target = await _sessions.ResolveCanonicalFollowupTargetAsync(projectId, sessionId, ct);
        if (target is null || string.IsNullOrWhiteSpace(target.RunnerId)
            || string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId))
            return;

        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        if (dispatch is null)
            return;

        var connectionId = _connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            return;
        }

        object binding = new
        {
            runtime = target.Runtime,
            runtimeSessionId = target.RuntimeSessionId,
            runnerId = target.RunnerId,
            workDir = target.WorkDir,
        };
        object wireTarget = string.Equals(target.SourceKind, "workflow", StringComparison.Ordinal)
            ? new
            {
                kind = "workflow",
                projectId,
                workflowRunId = target.WorkflowRunId,
                sessionName = target.SessionName,
                binding,
            }
            : new
            {
                kind = "generic",
                projectId,
                sessionId = target.SessionId,
                definition = target.Definition,
                binding,
            };
        var text = string.Join("\n", dispatch.InputTexts);
        var payload = new { target = wireTarget, text, operationId = dispatch.OperationId };

        try
        {
            var delivery = await _runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerFollowupDeliveryResult?>(
                "ReceiveFollowup", payload, ct);
            if (delivery?.Accepted != true)
                await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
        }
        catch
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
        }
    }
}
