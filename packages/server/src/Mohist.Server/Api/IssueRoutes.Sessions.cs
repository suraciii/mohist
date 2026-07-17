using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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
            string? runtimeSessionId,
            AgentSessionQuerier sessions) =>
        {
            var project = GetRequiredProject(ctx);
            var transcript = await sessions.GetSessionTranscriptAsync(project.Id, number, name, runtimeSessionId);
            return transcript is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(transcript);
        });

        group.MapPost("/{number:int}/sessions/{name}/compact", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            ISessionCommandDispatcher commands,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);
            var sessionId = await sessions.ResolveIssueSessionIdAsync(project.Id, number, name, ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {name} not found");

            return await AgentSessionRecoveryRoutes.ExecuteCompactAsync(
                sessionId, AgentSessionRecoveryRoutes.RecoveryIdempotencyKey(ctx), grains, commands, ct);
        });

        group.MapPost("/{number:int}/sessions/{name}/reset", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            ISessionCommandDispatcher commands,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);
            var sessionId = await sessions.ResolveIssueSessionIdAsync(project.Id, number, name, ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {name} not found");

            return await AgentSessionRecoveryRoutes.ExecuteResetAsync(
                sessionId,
                AgentSessionRecoveryRoutes.RecoveryIdempotencyKey(ctx),
                grains,
                commands,
                ct);
        });

        group.MapPost("/{number:int}/sessions/{name}/followup", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            FollowupRequest body,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections) =>
        {
            var text = body?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return ApiResults.BadRequest("text is required", "followup_text_missing");

            var project = GetRequiredProject(ctx);
            var sessionId = await sessions.ResolveIssueSessionIdAsync(project.Id, number, name, ctx.RequestAborted);
            if (sessionId is null)
                return ApiResults.NotFound($"Session {name} not found");

            return await AgentSessionFollowupRoutes.ExecuteFollowupAsync(
                project.Id,
                sessionId,
                text,
                sessions,
                grains,
                runnerHub,
                connections,
                ctx.RequestAborted);
        });

        group.MapPost("/{number:int}/sessions/{name}/cancel", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);
            var sessionId = await sessions.ResolveIssueSessionIdAsync(project.Id, number, name, ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {name} not found");

            return await AgentSessionCancelRoutes.ExecuteCancelAsync(
                project.Id,
                sessionId,
                sessions,
                grains,
                runnerHub,
                connections,
                ct);
        });
    }
}

public sealed record FollowupRequest(string? Text);
