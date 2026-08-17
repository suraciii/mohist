using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The <c>/api/v1</c> external Agent API boundary. Runs immediately
/// after <see cref="AuthResolutionMiddleware"/> for every direct API
/// path and enforces, strictly before any endpoint delegate runs:
/// <list type="number">
/// <item>the credential carrier is the Bearer header — an authenticated
/// cookie session (or any other non-Bearer identity) answers 401 with
/// the Bearer challenge;</item>
/// <item>the resolved credential is a PAT — a non-PAT credential
/// presented instead of a PAT (a runner, integration, or service file
/// credential) is unauthenticated here, indistinguishable from a
/// missing PAT;</item>
/// <item>the PAT carries a persisted, usable direct API grant —
/// otherwise 403;</item>
/// <item>the caller's scopes satisfy the route's
/// <see cref="RouteScopeRequirement"/> (writes need operator, reads
/// accept readonly or operator) — otherwise 403;</item>
/// <item>the route's <c>projectId</c> passes the persisted grant
/// (explicit list or operator_all) — otherwise 403, regardless of
/// whether the Project exists.</item>
/// </list>
/// The middleware never re-resolves the token: it reads the carrier and
/// caller facts the auth layer already recorded in
/// <see cref="HttpContext.Items"/>. Because steps 1–5 short-circuit
/// before the endpoint, 401/403 paths structurally cannot reach request
/// validation, idempotency, admission, or any effect.
/// </summary>
public sealed class ExternalAgentApiMiddleware : IMiddleware, IScopedService
{
    /// <summary>
    /// The single path prefix this middleware and the direct API route
    /// group are pinned to. The group registration
    /// (<see cref="DirectApiRoutes.MapDirectApiRoutes"/>) reuses this
    /// constant so the boundary and the routes cannot drift apart.
    /// </summary>
    public const string PathPrefix = "/api/v1";

    /// <summary>The route value every direct API template carries.</summary>
    public const string ProjectIdRouteName = "projectId";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        // 1. Bearer-only carrier: a cookie-resolved principal (the Web
        // session, or any other non-Bearer identity) is unauthenticated
        // here, indistinguishable from a missing credential.
        if (context.Items[CredentialCarrierResolution.HttpContextItemKey]
            is not CredentialCarrier.Bearer)
        {
            await DirectApiAuthResponses.WriteUnauthenticatedAsync(context);
            return;
        }

        // 2. The credential must be a PAT: the auth layer records an
        // ExternalAgentCaller for exactly that kind. Any other usable
        // credential presented instead of a PAT is unauthenticated on
        // this surface — the same non-classifying 401 as any other
        // request without a usable PAT.
        if (context.Items[ExternalAgentCaller.HttpContextItemKey]
            is not ExternalAgentCaller caller)
        {
            await DirectApiAuthResponses.WriteUnauthenticatedAsync(context);
            return;
        }

        // 3. The PAT's persisted direct API grant must be usable; a
        // grant-less PAT is forbidden, not unauthenticated.
        if (!caller.IsDirectApiEnabled)
        {
            await DirectApiAuthResponses.WriteForbiddenAsync(context);
            return;
        }

        // 4. Route scope, per operation class.
        var required = ResolveRequiredScopes(context);
        if (!ScopeSatisfaction.Satisfies(required, caller.Scopes, context.Request.Method))
        {
            await DirectApiAuthResponses.WriteForbiddenAsync(context);
            return;
        }

        // 5. Project grant, before any resource lookup: an out-of-grant
        // Project is forbidden even when it does not exist.
        if (context.Request.RouteValues[ProjectIdRouteName] is string projectId
            && !caller.AuthorizesProject(projectId))
        {
            await DirectApiAuthResponses.WriteForbiddenAsync(context);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// The route's declared scopes, or the method-based default for
    /// unmatched paths: GET is the observation surface (operator or
    /// readonly), every other method requires operator.
    /// </summary>
    private static IReadOnlyList<Scope> ResolveRequiredScopes(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<RouteScopeRequirement>();
        if (metadata is not null)
            return metadata.Scopes;

        return HttpMethods.IsGet(context.Request.Method)
            ? RouteScopeRequirementExtensions.OperatorOrReadonly
            : RouteScopeRequirementExtensions.Operator;
    }
}

public static class ExternalAgentApiMiddlewareExtensions
{
    /// <summary>
    /// Registers the direct external Agent API boundary. Must run after
    /// <see cref="AuthResolutionMiddleware"/> (it consumes that layer's
    /// recorded facts) and before the endpoint pipeline; the composition
    /// is pinned in <c>MohistHostFactory</c>.
    /// </summary>
    public static IApplicationBuilder UseExternalAgentApi(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ExternalAgentApiMiddleware>();
    }
}
