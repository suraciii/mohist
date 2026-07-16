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

            return await AgentSessionRecoveryRoutes.ExecuteCompactAsync(sessionId, grains, commands, ct);
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

            if (!string.IsNullOrWhiteSpace(target.TerminalState))
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
            RunnerFollowupDeliveryResult? delivery;
            try
            {
                delivery = await runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerFollowupDeliveryResult?>(
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
                    },
                    ctx.RequestAborted);
            }
            catch
            {
                return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
            }

            if (delivery?.Accepted == true)
                return ApiResults.Ok(new AgentSessionFollowupResult(sessionId));

            if (string.Equals(delivery?.Error, "missing", StringComparison.Ordinal))
            {
                return ApiResults.Conflict(
                    $"Runtime session missing for AgentSession {sessionId}. Reset the session to establish a new binding.",
                    "runtime_session_missing",
                    new { sessionId, hint = "reset" });
            }

            return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
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
