using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
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

        group.MapPost("/{number:int}/sessions/{name}/followup", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string name,
            FollowupRequest body,
            AgentSessionQuerier sessions,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections) =>
        {
            var text = body?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return ApiResults.BadRequest("text is required", "followup_text_missing");

            var project = GetRequiredProject(ctx);
            var target = await sessions.ResolveFollowupTargetAsync(project.Id, number, name, ctx.RequestAborted);
            if (target is null)
                return ApiResults.NotFound($"Session {name} not found");

            if (!target.IsActive)
                return ApiResults.Conflict("Session is no longer active", "session_inactive");

            var connectionId = connections.GetConnectionId(target.RunnerId);
            if (string.IsNullOrWhiteSpace(connectionId))
                return ApiResults.Fail("Runner is offline", 503, "runner_offline", new { runnerId = target.RunnerId });

            await runnerHub.Clients.Client(connectionId).SendAsync(
                "ReceiveFollowup",
                new { workflowRunId = target.WorkflowRunId, sessionName = target.SessionName, text });

            return ApiResults.Ok(new { status = "sent" });
        });
    }

    private static string BuildNewAgentSessionId() => Guid.NewGuid().ToString("N");
}

public sealed record FollowupRequest(string? Text);
