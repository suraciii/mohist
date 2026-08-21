using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Services;
using Mohist.Server.Slack.Services;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Independent Server-side admission for Manager-mode CLI requests. The
/// request is checked against the shared logical catalog before endpoint
/// filters resolve a Project or a handler looks up a target. Ordinary CLI,
/// Web, adapter, and API requests do not carry the Manager marker and keep
/// their existing route and authorization behavior.
/// </summary>
public sealed class ManagerCapabilityAdmissionMiddleware : IMiddleware, IScopedService
{
    private readonly ManagerActorAccessDecider _access;
    private readonly ProjectRefResolver _projects;

    public ManagerCapabilityAdmissionMiddleware(
        ManagerActorAccessDecider access,
        ProjectRefResolver projects)
    {
        _access = access;
        _projects = projects;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsManagerRequest(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var capability = context.Request.Path.StartsWithSegments(
                "/api/slack-manager/reply", StringComparison.Ordinal)
            ? ManagerCapabilityCatalog.ManagerReply
            : context.Request.Path.StartsWithSegments(
                "/api/slack-manager/management", StringComparison.Ordinal)
                ? ManagerCapabilityCatalog.ManagerManagementRoute
                : ManagerCapabilityCatalog.ResolveHttp(
                    context.Request.Method,
                    context.Request.Path.Value ?? string.Empty);
        if (!ManagerCapabilityCatalog.IsManagerCapability(capability))
        {
            await RejectAsync(context, "This operation is unavailable to Manager executions.",
                "manager_capability_not_available").ConfigureAwait(false);
            return;
        }

        // Authentication normally rejects a Manager-marked request before
        // this point. Test hosts and other auth adapters may deliberately
        // leave the request unauthenticated; preserve the route's normal
        // lookup semantics in that case.
        if (context.Items[ManagerExecutionCredentialContext.HttpContextItemKey]
            is not ManagerExecutionCredentialContext credential)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var authentication = await _access.AuthenticateAsync(
            credential.Lease.Origin.WorkspaceId,
            credential.Lease.Origin.ActorId,
            context.RequestAborted).ConfigureAwait(false);
        if (!authentication.Allowed || authentication.Actor is null)
        {
            await RejectAsync(context,
                "Manager authorization is no longer active; inspect current status and start a fresh turn.",
                "manager_actor_not_authorized").ConfigureAwait(false);
            return;
        }

        var requestedWorkspace = context.Request.Query["workspaceTeamId"].ToString();
        if (!string.IsNullOrWhiteSpace(requestedWorkspace)
            && !string.Equals(requestedWorkspace.Trim(), credential.Lease.Origin.WorkspaceId, StringComparison.Ordinal))
        {
            await RejectAsync(context,
                "The requested workspace is outside this Manager execution.",
                "manager_workspace_not_authorized").ConfigureAwait(false);
            return;
        }

        if (context.Request.RouteValues.TryGetValue("projectRef", out var rawProjectRef)
            && rawProjectRef is string projectRef
            && !string.IsNullOrWhiteSpace(projectRef))
        {
            var project = await _projects.ResolveAsync(projectRef).ConfigureAwait(false);
            if (project is null)
            {
                await ApiResults.NotFound("Project not found").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var decision = await _access.AuthorizeAsync(
                authentication.Actor,
                new ManagerResourceTarget(ManagerResourceKinds.Project, project.Id),
                context.RequestAborted).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                await RejectAsync(context,
                    "The requested Project is outside this Manager execution.",
                    decision.Reason ?? "manager_resource_not_found").ConfigureAwait(false);
                return;
            }

            if (context.Request.RouteValues.TryGetValue("connectionId", out var rawConnectionId)
                && rawConnectionId is string connectionId
                && !string.IsNullOrWhiteSpace(connectionId))
            {
                decision = await _access.AuthorizeAsync(
                    authentication.Actor,
                    new ManagerResourceTarget(ManagerResourceKinds.Connection, project.Id, connectionId),
                    context.RequestAborted).ConfigureAwait(false);
                if (!decision.Allowed)
                {
                    await RejectAsync(context,
                        "The requested Slack Connection is outside this Manager execution.",
                        decision.Reason ?? "manager_resource_not_found").ConfigureAwait(false);
                    return;
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static Task RejectAsync(HttpContext context, string message, string code) =>
        ApiResults.Fail(message, StatusCodes.Status403Forbidden, code)
            .ExecuteAsync(context);

    private static bool IsManagerRequest(HttpContext context) =>
        context.Request.Headers.TryGetValue(ManagerCapabilityCatalog.ManagerModeHeader, out var values)
        && values.Count == 1
        && ManagerCapabilityCatalog.IsManagerModeValue(values[0]);
}

public static class ManagerCapabilityAdmissionMiddlewareExtensions
{
    public static IApplicationBuilder UseManagerCapabilityAdmission(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ManagerCapabilityAdmissionMiddleware>();
    }
}
