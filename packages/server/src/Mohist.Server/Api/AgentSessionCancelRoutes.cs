using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services;
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
            WorkflowSessionWorkReconciler workReconciler,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return await ExecuteCancelAsync(project.Id, sessionId, request, sessions, grains, workReconciler, ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteCancelAsync(
        string projectId,
        string sessionId,
        AgentSessionCancelRequest? request,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        WorkflowSessionWorkReconciler workReconciler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TurnId))
            return ApiResults.BadRequest("turnId is required", "turn_id_missing");

        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return ApiResults.NotFound($"Agent session {sessionId} not found");

        var result = await AgentSessionTurnControlOperations.CancelAsync(grains, target.SessionId, request.TurnId);
        if (result.Kind is TurnControlResultKind.Cancelled or TurnControlResultKind.AlreadyEnded)
            await workReconciler.ReconcileAsync(projectId, target.SessionId, target.RunnerId, "session-cancel", ct);
        return result.Kind switch
        {
            TurnControlResultKind.NotFound => ApiResults.NotFound($"Turn {request.TurnId} not found"),
            TurnControlResultKind.Cancelled => ApiResults.Ok(new { state = "cancelled" }),
            TurnControlResultKind.AlreadyEnded => ApiResults.Ok(new
            {
                state = "turn-already-ended",
                turnStatus = result.StatusText ?? ToStatusString(result.Status!.Value),
            }),
            TurnControlResultKind.Executing => ApiResults.Ok(new { state = "executing", action = "stop" }),
            _ => throw new InvalidOperationException($"Unexpected cancel result {result.Kind}"),
        };
    }

    private static string ToStatusString(AgentTurnStatus status) => status.ToString().ToLowerInvariant();

    private static string ToStatusString(AgentJobStatus status) => status.ToString().ToLowerInvariant();
}

public sealed record AgentSessionCancelRequest(string? TurnId);

public sealed record AgentSessionCancelReply(string? State, bool? InterruptUnconfirmed = null);
