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

        var session = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        var claim = await session.ClaimTurnStopAsync(request.TurnId);
        var control = claim.Control;
        if (control is null)
            return ApiResults.NotFound($"Turn {request.TurnId} not found");

        if (control.Classification == AgentTurnControlClassification.Terminal)
        {
            return ApiResults.Ok(new
            {
                state = "turn-already-ended",
                turnStatus = control.Status.ToString().ToLowerInvariant(),
            });
        }

        if (control.Classification == AgentTurnControlClassification.Queued)
            return ApiResults.Ok(new { state = "queued", action = "cancel" });

        if (!claim.CanDispatch)
            return ApiResults.Ok(new { state = "stop-requested" });

        var runnerId = connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId)
            || string.IsNullOrWhiteSpace(target.WorkDir))
        {
            await session.CompleteTurnStopAsync(control.TurnId);
            return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
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
                binding,
            };

        RunnerStopReply? reply;
        try
        {
            reply = await runnerHub.Clients.Client(runnerId).InvokeAsync<RunnerStopReply?>(
                "CancelAgentSession",
                new { target = wireTarget, turnId = request.TurnId },
                ct);
        }
        catch
        {
            return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
        }

        if (reply is null)
            return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });

        if (control.IsLaunchTurn
            && string.Equals(reply.State, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            await grains.GetGrain<IAgentJobGrain>(control.JobId!).MarkUnknownAsync("stop-unconfirmed");
        }
        else if (string.Equals(reply.State, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            await session.MarkTurnTerminalAsync(control.TurnId, AgentTurnStatus.Unknown, null);
        }

        await session.CompleteTurnStopAsync(control.TurnId);

        return ApiResults.Ok(new
        {
            state = reply.State,
            interruptUnconfirmed = reply.InterruptUnconfirmed,
        });
    }
}

public sealed record RunnerStopReply(string? State, bool? InterruptUnconfirmed = null);
