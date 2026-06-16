using Microsoft.AspNetCore.Routing;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Grains;
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

        group.MapPost("/{number:int}/sessions/{name}/compact", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);
            var sessionId = await sessions.ResolveIssueSessionIdAsync(project.Id, number, name, ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {name} not found");

            var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
            try
            {
                var result = await grain.CompactAsync(new CompactAgentSessionCommand(NewAgentSessionId: BuildNewAgentSessionId()));
                return ApiResults.Ok(result);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.Conflict("Cannot compact while session is active", "session_active", new { sessionId });
            }
        });

        group.MapPost("/{number:int}/sessions/{name}/reset", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);
            var sessionId = await sessions.ResolveIssueSessionIdAsync(project.Id, number, name, ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {name} not found");

            var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
            try
            {
                var result = await grain.ResetAsync(new ResetAgentSessionCommand(NewAgentSessionId: BuildNewAgentSessionId()));
                return ApiResults.Ok(result);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.Conflict("Cannot reset while session is active", "session_active", new { sessionId });
            }
        });
    }

    private static string BuildNewAgentSessionId() => Guid.NewGuid().ToString("N");
}
