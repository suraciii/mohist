using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public static class AgentSessionDetachRoutes
{
    public static WebApplication MapAgentSessionDetachRoutes(this WebApplication app)
    {
        var group = app.MapGroup(AgentSessionStopRoutes.PathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{childSessionId}/detach", async (
            HttpContext context,
            string childSessionId,
            SessionTreeDetachService detach,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var result = await detach.DetachAsync(project.Id, childSessionId, ct);
            if (result is null)
                return ApiResults.NotFound($"Agent session {childSessionId} not found");
            if (result.State == SessionTreeDetachResultState.ReconciliationRequired)
                return ApiResults.Conflict(
                    result.Reason ?? "Session tree detach requires reconciliation.",
                    "reconciliation_required",
                    result);
            return ApiResults.Ok(result);
        });

        return app;
    }
}
