using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Read-only endpoints that surface the generic <c>agent-launch</c>
/// <see cref="Sessions.Domain.AgentSession"/>s associated with an issue
/// or epic via their recorded <c>agent-launch/issue-number</c> or
/// <c>agent-launch/epic-number</c> context-reference labels
/// (issue-130 T-006). Each returns a lightweight association list where
/// every entry carries the session id, agent id, agent name, status,
/// created timestamp, and a link back to the session summary route.
/// These endpoints are pure reads — they perform no writes and create
/// no scope, mount, supervisor, ownership, or workflow lifecycle.
/// </summary>
public static class AgentSessionContextAssociationRoutes
{
    public static WebApplication MapAgentSessionContextAssociationRoutes(this WebApplication app)
    {
        app.MapIssueAgentSessions();
        app.MapEpicAgentSessions();
        return app;
    }

    private static WebApplication MapIssueAgentSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/issues/{number}/agent-sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext context,
            string projectRef,
            int number,
            IssueQuerier issues,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            if (await issues.GetAsync(project.Id, number) is null)
                return ApiResults.NotFound($"Issue #{number} not found");
            var items = await sessions.ListSessionsByContextRefAsync(
                project.Id,
                projectRef,
                GenericAgentSessionMetadata.IssueNumber,
                number.ToString(),
                ct);
            return ApiResults.Ok(items);
        });

        return app;
    }

    private static WebApplication MapEpicAgentSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/epics/{number:int}/agent-sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext context,
            string projectRef,
            int number,
            EpicQuerier epics,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            if (await epics.GetAsync(project.Id, number) is null)
                return ApiResults.NotFound($"Epic #{number} not found");

            var items = await sessions.ListSessionsByContextRefAsync(
                project.Id,
                projectRef,
                GenericAgentSessionMetadata.EpicNumber,
                number.ToString(),
                ct);
            return ApiResults.Ok(items);
        });

        return app;
    }
}
