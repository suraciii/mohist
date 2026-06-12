using Microsoft.AspNetCore.Routing;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Services.Sessions;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueSessions(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/coder-sessions", async (
            HttpContext ctx,
            string projectRef,
            int number,
            AgentSessionQuerier sessions) =>
        {
            var project = GetRequiredProject(ctx);
            return ApiResults.Ok(await sessions.ListSummariesByIssueAsync(project.Id, number));
        });

        group.MapGet("/{number:int}/sessions/{name}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions) =>
        {
            var project = GetRequiredProject(ctx);
            var metadata = await sessions.GetSessionMetadataAsync(project.Id, number, name);
            return metadata is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(metadata);
        });

        group.MapGet("/{number:int}/sessions/{name}/transcript", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions) =>
        {
            var project = GetRequiredProject(ctx);
            var transcript = await sessions.GetSessionTranscriptAsync(project.Id, number, name);
            return transcript is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(transcript);
        });
    }
}
