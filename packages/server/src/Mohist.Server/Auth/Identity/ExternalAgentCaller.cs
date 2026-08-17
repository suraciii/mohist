using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Mohist.Server.Auth.Domain;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The credential-scoped identity accepted by the external Agent API.
/// This remains separate from the general request principal because direct
/// routes require an issued PAT and an explicit durable Project grant.
/// </summary>
public sealed record ExternalAgentCaller(
    string CallerKeyId,
    string PrincipalId,
    IReadOnlyList<Scope> Scopes,
    DirectApiProjectGrant? ProjectGrant)
{
    public const string HttpContextItemKey = "mohist.externalAgentCaller";

    public string CredentialId => CallerKeyId;

    public bool IsDirectApiEnabled => ProjectGrant is { IsValid: true };

    public bool AuthorizesProject(string projectId) => ProjectGrant is { IsValid: true } grant
        && grant.Kind switch
        {
            DirectApiProjectGrantKind.OperatorAll => true,
            DirectApiProjectGrantKind.Explicit => grant.AllowedProjectIds.Contains(projectId),
            _ => false,
        };

    public bool AllowsProject(string projectId) => ProjectGrant?.Kind switch
    {
        DirectApiProjectGrantKind.OperatorAll => true,
        DirectApiProjectGrantKind.Explicit => ProjectGrant.AllowedProjectIds.Contains(
            projectId,
            StringComparer.Ordinal),
        _ => false,
    };
}

/// <summary>
/// Checks the requested canonical Project id before a direct handler can
/// inspect its resource. Project resolution is intentionally not part of this
/// gate: explicit grants already contain canonical ids, while operator-wide
/// grants authorize the route's Project namespace without an extra lookup.
/// </summary>
public sealed class ExternalAgentProjectGrantEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (context.HttpContext.Items[ExternalAgentCaller.HttpContextItemKey] is not ExternalAgentCaller caller)
        {
            return ValueTask.FromResult<object?>(ApiResults.Fail(
                "Authentication required.",
                StatusCodes.Status401Unauthorized,
                "unauthorized"));
        }

        var projectId = context.HttpContext.Request.RouteValues.TryGetValue("projectId", out var routeValue)
            ? routeValue as string
            : null;
        if (string.IsNullOrWhiteSpace(projectId) || !caller.AllowsProject(projectId))
        {
            return ValueTask.FromResult<object?>(ApiResults.Fail(
                "The credential is not granted access to this Project.",
                StatusCodes.Status403Forbidden,
                "forbidden"));
        }

        return next(context);
    }
}
