using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Generic (non-workflow) AgentSession followup endpoint for issue-129 T-004.
/// Distinct from the issue-scoped
/// <c>POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup</c>
/// route (<see cref="IssueRoutes.MapIssueSessions"/>), which remains
/// unchanged and reachable via the existing workflowRunId + sessionName axis.
/// Generic sessions are reached by their minted <c>sessionId</c> alone
/// (no workflowRunId lookup key); the launch endpoint stamps
/// <c>source-kind = agent-launch</c> labels and the resolver in
/// <see cref="AgentSessionQuerier.ResolveGenericFollowupTargetAsync"/> reads
/// the runner id from the session's Runtime state.
/// </summary>
public static class AgentSessionFollowupRoutes
{
    public const string FollowupPathPrefix = "/api/projects/{projectRef}/agent-sessions";

    public static WebApplication MapAgentSessionFollowupRoutes(this WebApplication app)
    {
        var group = app.MapGroup(FollowupPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/{sessionId}", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var summary = await sessions.GetGenericSessionSummaryAsync(project.Id, sessionId, ct);
            return summary is null
                ? ApiResults.NotFound($"Agent session {sessionId} not found")
                : ApiResults.Ok(summary);
        });

        group.MapGet("/{sessionId}/transcript", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var transcript = await sessions.GetGenericSessionTranscriptAsync(project.Id, sessionId, ct);
            return transcript is null
                ? ApiResults.NotFound($"Agent session {sessionId} not found")
                : ApiResults.Ok(transcript);
        });

        group.MapPost("/{sessionId}/followup", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            GenericFollowupRequest body,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections,
            CancellationToken ct) =>
        {
            var text = body?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return ApiResults.BadRequest("text is required", "followup_text_missing");

            var project = context.GetResolvedProject();
            var target = await sessions.ResolveGenericFollowupTargetAsync(project.Id, sessionId, ct);
            if (target is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

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

            if (!target.IsActive)
                return ApiResults.Conflict("Session is no longer active", "session_inactive");

            // IsActive=true implies a runner has opened and reported
            // runtime events, so RunnerId is guaranteed to be present.
            // Defensive guard for the (impossible-by-construction) case
            // where a session reads as active without a bound runner —
            // surface it as 503 (the runner lookup will fail) rather than
            // crash the handler.
            if (string.IsNullOrWhiteSpace(target.RunnerId))
                return ApiResults.Fail(
                    "Runner is offline",
                    503,
                    "runner_offline",
                    new { runnerId = target.RunnerId ?? string.Empty });

            var connectionId = connections.GetConnectionId(target.RunnerId);
            if (string.IsNullOrWhiteSpace(connectionId))
                return ApiResults.Fail(
                    "Runner is offline",
                    503,
                    "runner_offline",
                    new { runnerId = target.RunnerId });

            await runnerHub.Clients.Client(connectionId).SendAsync(
                "ReceiveFollowup",
                new
                {
                    target = new
                    {
                        kind = "generic",
                        projectId = project.Id,
                        sessionId = target.SessionId,
                    },
                    text,
                });

            return ApiResults.Ok(new { status = "sent" });
        });

        return app;
    }
}

/// <summary>
/// Body for <c>POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup</c>.
/// <see cref="Text"/> is required and must be non-empty; whitespace-only text
/// is rejected with 400 (<c>followup_text_missing</c>) before any session
/// or runner lookup, mirroring the issue-scoped followup body shape.
/// </summary>
public sealed record GenericFollowupRequest(string? Text = null);
