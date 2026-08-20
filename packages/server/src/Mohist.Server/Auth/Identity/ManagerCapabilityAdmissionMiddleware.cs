using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Hosting;
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
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsManagerRequest(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var capability = ManagerCapabilityCatalog.ResolveHttp(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty);
        if (!ManagerCapabilityCatalog.IsManagement(capability))
        {
            await ApiResults.Fail(
                "This operation is unavailable to Manager executions.",
                StatusCodes.Status403Forbidden,
                "manager_capability_not_available").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

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
