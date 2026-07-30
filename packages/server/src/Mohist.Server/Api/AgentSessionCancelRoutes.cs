using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Canonical AgentSession cancel endpoint for either source, addressed by the
/// stable session id and a durable Turn id. Cancel is a Server-only transition
/// and never contacts the Runner or Runtime.
/// </summary>
/// <remarks>
/// </remarks>
public static class AgentSessionCancelRoutes
{
    public const string CancelPathPrefix = "/api/projects/{projectRef}/agent-sessions";

    public static WebApplication MapAgentSessionCancelRoutes(this WebApplication app)
    {
        var group = app.MapGroup(CancelPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/cancel", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionCancelRequest? request,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return await ExecuteCancelAsync(project.Id, sessionId, request, sessions, grains, ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteCancelAsync(
        string projectId,
        string sessionId,
        AgentSessionCancelRequest? request,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TurnId))
            return ApiResults.BadRequest("turnId is required", "turn_id_missing");

        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return ApiResults.NotFound($"Agent session {sessionId} not found");

        var session = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        var cancellation = await session.CancelQueuedTurnAsync(request.TurnId);
        var control = cancellation.Control;
        if (control is null)
            return ApiResults.NotFound($"Turn {request.TurnId} not found");

        if (cancellation.Cancelled)
            return ApiResults.Ok(new { state = "cancelled" });

        if (control.Classification == AgentTurnControlClassification.Terminal)
        {
            return ApiResults.Ok(new
            {
                state = "turn-already-ended",
                turnStatus = ToStatusString(control.Status),
            });
        }

        if (control.Classification == AgentTurnControlClassification.Executing)
        {
            return ApiResults.Ok(new { state = "executing", action = "stop" });
        }

        if (control.IsLaunchTurn)
        {
            var result = await grains.GetGrain<IAgentJobGrain>(control.JobId!).CancelAsync();
            return result.Disposition switch
            {
                AgentJobCancelDisposition.Cancelled => ApiResults.Ok(new { state = "cancelled" }),
                AgentJobCancelDisposition.Executing => ApiResults.Ok(new { state = "executing", action = "stop" }),
                _ => ApiResults.Ok(new
                {
                    state = "turn-already-ended",
                    turnStatus = ToStatusString(result.Status),
                }),
            };
        }

        return ApiResults.Ok(new { state = "cancelled" });
    }

    private static string ToStatusString(AgentTurnStatus status) => status.ToString().ToLowerInvariant();

    private static string ToStatusString(AgentJobStatus status) => status.ToString().ToLowerInvariant();
}

public sealed record AgentSessionCancelRequest(string? TurnId);

public sealed record AgentSessionCancelReply(string? State, bool? InterruptUnconfirmed = null);
