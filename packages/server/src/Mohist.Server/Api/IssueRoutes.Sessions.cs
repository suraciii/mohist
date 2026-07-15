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
                var result = await grain.CompactAsync(new CompactAgentSessionCommand());
                return ApiResults.Ok(result);
            }
            catch (RuntimeSessionMissingException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "runtime_session_missing",
                    new { sessionId = ex.SessionId, hint = "reset" });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.Conflict(ex.Message, "session_active", new { sessionId });
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
                var current = await grain.GetAsync();
                if (current is null) return ApiResults.NotFound($"Session {name} not found");

                var result = await grain.ResetAsync(new ResetAgentSessionCommand(
                    ExpectedRuntimeSessionId: current.AgentSessionId,
                    ReplacementRuntimeSessionId: BuildNewAgentSessionId()));
                return ApiResults.Ok(result);
            }
            catch (RuntimeSessionMissingException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "runtime_session_missing",
                    new { sessionId = ex.SessionId, hint = "reset" });
            }
            catch (StaleRuntimeSessionBindingException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "stale_binding",
                    new
                    {
                        sessionId = ex.SessionId,
                        actualRuntimeSessionId = ex.ActualRuntimeSessionId,
                    });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.Conflict(ex.Message, "session_active", new { sessionId });
            }
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

            try
            {
                await grains.GetGrain<IAgentSessionGrain>(sessionId).EnsureRuntimeSessionPresentAsync();
            }
            catch (RuntimeSessionMissingException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "runtime_session_missing",
                    new { sessionId = ex.SessionId, hint = "reset" });
            }

            var target = await sessions.ResolveFollowupTargetAsync(project.Id, number, name, ctx.RequestAborted);
            if (target is null)
                return ApiResults.NotFound($"Session {name} not found");

            if (!target.IsActive)
                return ApiResults.Conflict("Session is no longer active", "session_inactive");

            var connectionId = connections.GetConnectionId(target.RunnerId);
            if (string.IsNullOrWhiteSpace(connectionId))
                return ApiResults.Fail("Runner is offline", 503, "runner_offline", new { runnerId = target.RunnerId });

            // Workflow followup payload: keep `workflowRunId` / `sessionName`
            // populated on the top level so older runners (that branch on
            // those fields) continue to work, AND emit the unified
            // `target: SessionTarget` shape (issue-129 T-004 / D3) so the
            // newer runner can route by target.kind. The runner resolver
            // prefers `target` when present and falls back to the
            // top-level workflow fields for backwards compatibility.
            await runnerHub.Clients.Client(connectionId).SendAsync(
                "ReceiveFollowup",
                new
                {
                    workflowRunId = target.WorkflowRunId,
                    sessionName = target.SessionName,
                    target = new
                    {
                        kind = "workflow",
                        projectId = project.Id,
                        workflowRunId = target.WorkflowRunId,
                        sessionName = target.SessionName,
                    },
                    text,
                });

            return ApiResults.Ok(new { status = "sent" });
        });
    }

    private static string BuildNewAgentSessionId() => Guid.NewGuid().ToString("N");
}

public sealed record FollowupRequest(string? Text);
