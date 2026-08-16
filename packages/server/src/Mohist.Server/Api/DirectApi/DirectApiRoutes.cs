using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.PublicApi;

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
/// The read delegates are served only from the persisted public
/// projection (the shared lag-checked read service). The write and
/// event-stream templates still answer with the placeholder result
/// until their endpoint delegates land; they replace it in place,
/// without altering this registration's order or metadata.
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

        // Reads: readonly or operator. Each read is served only from
        // the persisted public projection through the shared read
        // service: canonical Project membership answers the route's
        // 404 resource code, and a checkpoint that has not consumed
        // the anchor's durable source facts yet answers 503
        // projection_lag instead of a stale snapshot.
        group.MapGet("/agent-jobs/{jobId}", async (
            string projectId,
            string jobId,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            DirectApiResults.PublicRead(
                await publicReads.ReadJobAsync(projectId, jobId, ct),
                DirectApiErrorCodes.JobNotFound))
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-inputs/{inputId}", async (
            string projectId,
            string inputId,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            DirectApiResults.PublicRead(
                await publicReads.ReadInputAsync(projectId, inputId, ct),
                DirectApiErrorCodes.InputNotFound))
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-turns/{turnId}", async (
            string projectId,
            string turnId,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            DirectApiResults.PublicRead(
                await publicReads.ReadTurnAsync(projectId, turnId, ct),
                DirectApiErrorCodes.TurnNotFound))
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-sessions/{sessionId}/events", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Readonly, Scope.Operator);

        return app;
    }
}
