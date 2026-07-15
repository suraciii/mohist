using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Canonical Compact and Reset routes for AgentSessions from either source.
/// Workflow-scoped aliases resolve their lookup keys to a stable session id
/// and delegate to the same command executors used by these routes.
/// Followup and Cancel already preserve that canonical AgentSession identity;
/// their existing runner transport remains outside recovery routing.
/// </summary>
public static class AgentSessionRecoveryRoutes
{
    public const string RecoveryPathPrefix = "/api/projects/{projectRef}/agent-sessions";

    public static WebApplication MapAgentSessionRecoveryRoutes(this WebApplication app)
    {
        var group = app.MapGroup(RecoveryPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/compact", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionResolver sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var canonicalSessionId = await sessions.ResolveCanonicalIdAsync(project.Id, sessionId, ct);
            if (canonicalSessionId is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            return await ExecuteCompactAsync(canonicalSessionId, grains);
        });

        group.MapPost("/{sessionId}/reset", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionResolver sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var canonicalSessionId = await sessions.ResolveCanonicalIdAsync(project.Id, sessionId, ct);
            if (canonicalSessionId is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            return await ExecuteResetAsync(
                canonicalSessionId,
                grains,
                $"Agent session {sessionId} not found");
        });

        return app;
    }

    internal static async Task<IResult> ExecuteCompactAsync(string sessionId, IGrainFactory grains)
    {
        var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
        try
        {
            var result = await grain.CompactAsync(new CompactAgentSessionCommand());
            return ApiResults.Ok(result);
        }
        catch (RuntimeSessionMissingException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "runtime_session_missing",
                new { sessionId = ex.SessionId, hint = "reset" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResults.Conflict(ex.Message, "session_active", new { sessionId });
        }
    }

    internal static async Task<IResult> ExecuteResetAsync(
        string sessionId,
        IGrainFactory grains,
        string notFoundMessage)
    {
        var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
        try
        {
            var current = await grain.GetAsync();
            if (current is null) return ApiResults.NotFound(notFoundMessage);

            var expectedRuntimeSessionId = current.AgentSessionId;
            var result = await grain.ResetAsync(new ResetAgentSessionCommand(
                ExpectedRuntimeSessionId: expectedRuntimeSessionId,
                ReplacementRuntimeSessionId: expectedRuntimeSessionId!));
            return ApiResults.Ok(result);
        }
        catch (RuntimeSessionMissingException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "runtime_session_missing",
                new { sessionId = ex.SessionId, hint = "reset" });
        }
        catch (StaleRuntimeSessionBindingException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "stale_binding",
                new
                {
                    sessionId = ex.SessionId,
                    actualRuntimeSessionId = ex.ActualRuntimeSessionId,
                });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResults.Conflict(ex.Message, "session_active", new { sessionId });
        }
    }
}
