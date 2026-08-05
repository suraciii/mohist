using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public static class AgentSessionTreeRoutes
{
    public static WebApplication MapAgentSessionTreeRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agent-sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/{rootSessionId}/tree", async (
            HttpContext context,
            string rootSessionId,
            int? limit,
            string? continuation,
            AgentSessionTreeQuerier trees,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            try
            {
                var page = await trees.GetAsync(
                    project.Id,
                    rootSessionId,
                    limit ?? 50,
                    continuation,
                    ct);
                return page is null ? ApiResults.NotFound("Agent session tree root not found") : Results.Ok(page);
            }
            catch (AgentSessionTreeContinuationException)
            {
                return ApiResults.BadRequest("continuation is invalid", "invalid_continuation");
            }
        });

        return app;
    }
}
