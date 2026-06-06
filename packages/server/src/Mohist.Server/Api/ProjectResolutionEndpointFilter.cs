using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Endpoint filter that resolves the <c>{projectRef}</c> route value to a
/// <see cref="ProjectInfo"/> and short-circuits with the appropriate error
/// response when the project cannot be resolved. The resolved
/// <see cref="ProjectInfo"/> is stashed in <c>HttpContext.Items</c> under
/// <see cref="ProjectInfoItemKey"/> so downstream handlers can read it
/// without an extra round-trip to the resolver.
/// </summary>
/// <remarks>
/// Apply this filter once on the route group that owns the
/// <c>/api/projects/{projectRef}/...</c> prefix; every endpoint on the group
/// then inherits the same resolution + 404 behaviour.
///
/// Response semantics (kept identical to the previous inline checks):
/// <list type="bullet">
///   <item><description>empty/whitespace <c>projectRef</c> → 400 BadRequest("No active project")</description></item>
///   <item><description>project not found → 404 NotFound("Project not found")</description></item>
///   <item><description>resolved → <c>ProjectInfo</c> placed in <c>HttpContext.Items</c></description></item>
/// </list>
/// </remarks>
public sealed class ProjectResolutionEndpointFilter : IEndpointFilter
{
    public const string ProjectInfoItemKey = "Mohist.ProjectInfo";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.RouteValues.TryGetValue("projectRef", out var raw)
            || raw is not string projectRef)
        {
            // Endpoint is not under a {projectRef} prefix — skip resolution.
            return await next(context);
        }

        if (string.IsNullOrWhiteSpace(projectRef))
        {
            return ApiResults.BadRequest("No active project");
        }

        var resolver = context.HttpContext.RequestServices.GetRequiredService<ProjectRefResolver>();
        var project = await resolver.ResolveAsync(projectRef);
        if (project is null)
        {
            return ApiResults.NotFound("Project not found");
        }

        context.HttpContext.Items[ProjectInfoItemKey] = project;
        return await next(context);
    }
}

/// <summary>
/// Helpers to read the project info resolved by
/// <see cref="ProjectResolutionEndpointFilter"/>.
/// </summary>
public static class ProjectResolutionHttpContextExtensions
{
    /// <summary>
    /// Returns the <see cref="ProjectInfo"/> previously resolved by
    /// <see cref="ProjectResolutionEndpointFilter"/>. Throws if the
    /// filter has not run for the current request — that almost always
    /// means the route group forgot to apply the filter.
    /// </summary>
    public static ProjectInfo GetResolvedProject(this HttpContext context)
    {
        if (context.Items.TryGetValue(ProjectResolutionEndpointFilter.ProjectInfoItemKey, out var value)
            && value is ProjectInfo project)
        {
            return project;
        }

        throw new InvalidOperationException(
            "ProjectInfo is not present in HttpContext.Items. " +
            "Did the route forget to apply ProjectResolutionEndpointFilter?");
    }
}
