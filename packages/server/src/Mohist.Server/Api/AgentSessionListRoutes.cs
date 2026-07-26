using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Agent-scoped session list endpoint. Returns generic
/// <c>agent-launch</c> sessions for the resolved Agent profile, ordered by
/// recency with an optional <c>?status=</c> filter and <c>?limit=</c> cap.
/// Distinct from the project-wide <c>GET .../agent/sessions</c> list
/// (<see cref="AgentRoutes"/>) and the workflow list
/// <c>.../issues/{n}/sessions</c>.
/// </summary>
public static class AgentSessionListRoutes
{
    public static WebApplication MapAgentSessionListRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents/{agentRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/sessions", async (
            HttpContext context,
            string projectRef,
            string agentRef,
            string? status,
            int? limit,
            AgentQuerier agentQuerier,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();

            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            IReadOnlyCollection<string>? statusSet = null;
            if (!string.IsNullOrWhiteSpace(status))
                statusSet = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var items = await sessions.ListAgentSessionsAsync(
                project.Id,
                agent.Id,
                statusSet,
                limit ?? 50,
                ct: ct);

            return ApiResults.Ok(items);
        });

        return app;
    }
}
