using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public static class AgentSessionStopRoutes
{
    public static WebApplication MapAgentSessionStopRoutes(this WebApplication app)
    {
        var group = app.MapGroup(AgentSessionCancelRoutes.CancelPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/stop", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionCancelRequest? request,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return await ExecuteStopAsync(
                project.Id,
                sessionId,
                request,
                sessions,
                grains,
                runnerHub,
                connections,
                ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteStopAsync(
        string projectId,
        string sessionId,
        AgentSessionCancelRequest? request,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TurnId))
            return ApiResults.BadRequest("turnId is required", "turn_id_missing");

        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return ApiResults.NotFound($"Agent session {sessionId} not found");

        var result = await AgentSessionTurnControlOperations.StopAsync(
            projectId, grains, runnerHub, connections, target, request.TurnId, ct);
        return result.Kind switch
        {
            TurnControlResultKind.NotFound => ApiResults.NotFound($"Turn {request.TurnId} not found"),
            TurnControlResultKind.AlreadyEnded => ApiResults.Ok(new
            {
                state = "turn-already-ended",
                turnStatus = result.Status!.Value.ToString().ToLowerInvariant(),
            }),
            TurnControlResultKind.Queued => ApiResults.Ok(new { state = "queued", action = "cancel" }),
            TurnControlResultKind.StopRequested => ApiResults.Ok(new { state = "stop-requested" }),
            TurnControlResultKind.RunnerUnavailable => ApiResults.Fail(
                "Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId }),
            _ => ApiResults.Ok(new
            {
                state = result.Kind switch
                {
                    TurnControlResultKind.Stopped => "stopped",
                    TurnControlResultKind.Unknown => "unknown",
                    TurnControlResultKind.NotCancellable => "not-cancellable",
                    _ => throw new InvalidOperationException($"Unexpected stop result {result.Kind}"),
                },
                interruptUnconfirmed = result.InterruptUnconfirmed,
            }),
        };
    }
}

public sealed record RunnerStopReply(string? State, bool? InterruptUnconfirmed = null);
