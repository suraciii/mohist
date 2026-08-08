using Microsoft.AspNetCore.Http;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The per-project narrowing hook for integration credentials: at
/// resolution the middleware extracts the request's project ref — the
/// <c>{projectRef}</c> route value, else the <c>projectRef</c> query
/// parameter — resolves it and records the constraint evaluation on the
/// request. The denial itself (a request outside the constrained project
/// is rejected) is the P2 scope gate; this type is the evaluation that
/// gate consumes, so the narrowing judgment stays P2 while the mechanism
/// ships now.
/// </summary>
public static class IntegrationProjectConstraint
{
    public const string ItemKey = "mohist.integrationConstraint";

    private const string ProjectRefRouteValue = "projectRef";
    private const string ProjectRefQueryKey = "projectRef";

    /// <summary>
    /// The request's project ref from the <c>{projectRef}</c> route
    /// value (inbound integration endpoints live under
    /// <c>/api/projects/&#123;projectRef&#125;/...</c>) or the
    /// <c>projectRef</c> query parameter, or null when the request does
    /// not target a project.
    /// </summary>
    public static string? ExtractProjectRef(HttpRequest request)
    {
        if (request.RouteValues.TryGetValue(ProjectRefRouteValue, out var routeValue)
            && routeValue is string routeRef
            && !string.IsNullOrWhiteSpace(routeRef))
        {
            return routeRef;
        }

        if (request.Query.TryGetValue(ProjectRefQueryKey, out var queryValues)
            && queryValues.Count > 0
            && !string.IsNullOrWhiteSpace(queryValues[0]))
        {
            return queryValues[0]!;
        }

        return null;
    }

    public static bool IsSatisfied(string? constrainedProjectId, string? requestProjectId) =>
        constrainedProjectId is not null
        && requestProjectId is not null
        && string.Equals(constrainedProjectId, requestProjectId, StringComparison.Ordinal);

    /// <summary>
    /// The constraint evaluation recorded on the request when an
    /// integration credential authenticates: the project the credential
    /// is narrowed to versus the project the request targets. A request
    /// without a project ref resolves to a null request project, which
    /// never satisfies a constraint.
    /// </summary>
    public sealed record Resolution(string? ConstrainedProjectId, string? RequestProjectId)
    {
        public bool IsSatisfied =>
            IntegrationProjectConstraint.IsSatisfied(ConstrainedProjectId, RequestProjectId);
    }
}
