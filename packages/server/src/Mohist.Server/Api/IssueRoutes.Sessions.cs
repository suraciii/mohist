using Microsoft.AspNetCore.Routing;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueSessions(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/coder-sessions", async (
            HttpContext ctx,
            string projectRef,
            int number,
            AgentSessionQuerier sessions,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            return ApiResults.Ok(await sessions.ListSummariesByIssueAsync(project.Id, number));
        });

        group.MapGet("/{number:int}/sessions/{name}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var metadata = await sessions.GetSessionMetadataAsync(project.Id, number, name);
            return metadata is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(metadata);
        });

        group.MapGet("/{number:int}/sessions/{name}/events", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var events = await sessions.GetSessionEventsAsync(project.Id, number, name);
            return events is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(events);
        });
    }
}
