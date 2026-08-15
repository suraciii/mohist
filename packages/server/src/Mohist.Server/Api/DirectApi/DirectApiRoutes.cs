using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The one registration of the direct external Agent API route group
/// (the public v1 HTTP surface): all seven v1 route templates
/// under <c>/api/v1/projects/{projectId}/…</c>, each carrying its
/// <see cref="RouteScopeRequirement"/> metadata — writes (launch,
/// follow-up, stop) require <c>operator</c>, reads (Job, Input, Turn,
/// Session events) accept <c>readonly</c> or <c>operator</c>.
/// <para>
/// The authorization pipeline in front of these routes —
/// <see cref="ExternalAgentApiMiddleware"/> pinned to
/// <see cref="ExternalAgentApiMiddleware.PathPrefix"/> — is the shipped
/// boundary and must not change as endpoint delegates land: carrier,
/// grant, scope, and Project authorization are settled before any
/// delegate runs.
/// </para>
/// <para>
/// The delegates registered here are placeholders: they only prove the
/// pipeline passed. The follow-up implementation tasks replace them
/// with the public read, idempotent write, and event-stream handlers
/// in place, without altering this registration's order or metadata.
/// </para>
/// </summary>
public static class DirectApiRoutes
{
    public static WebApplication MapDirectApiRoutes(this WebApplication app)
    {
        var group = app.MapGroup(
            ExternalAgentApiMiddleware.PathPrefix + "/projects/{" + ExternalAgentApiMiddleware.ProjectIdRouteName + "}");

        // Writes: operator only.
        group.MapPost("/agents/{agentId}/launch", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Operator);
        group.MapPost("/agent-sessions/{sessionId}/inputs", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Operator);
        group.MapPost("/agent-turns/{turnId}/stop", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Operator);

        // Reads: readonly or operator.
        group.MapGet("/agent-jobs/{jobId}", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-inputs/{inputId}", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-turns/{turnId}", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-sessions/{sessionId}/events", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Readonly, Scope.Operator);

        return app;
    }
}
