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
/// Canonical follow-up endpoint for AgentSessions from either source.
/// Follow-up joins the active turn or starts a user-initiated turn when the
/// session is idle; neither case creates a TaskRun or AgentJob. The issue-scoped
/// <c>POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup</c>
/// route (<see cref="IssueRoutes.MapIssueSessions"/>) is a Workflow lookup
/// alias that resolves to the same stable AgentSession id and returns the same
/// <see cref="AgentSessionFollowupResult"/> shape before using its
/// Workflow-shaped runner target. The resolver in
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
            string? runtimeSessionId,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var transcript = await sessions.GetGenericSessionTranscriptAsync(project.Id, sessionId, runtimeSessionId, ct);
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

            if (!string.IsNullOrWhiteSpace(target.TerminalState))
                return ApiResults.Conflict("Session is no longer active", "session_inactive");

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

            RunnerFollowupDeliveryResult? delivery;
            try
            {
                delivery = await runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerFollowupDeliveryResult?>(
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
                    },
                    ct);
            }
            catch
            {
                return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
            }

            if (delivery?.Accepted == true)
                return ApiResults.Ok(new AgentSessionFollowupResult(target.SessionId));

            if (string.Equals(delivery?.Error, "missing", StringComparison.Ordinal))
            {
                return ApiResults.Conflict(
                    $"Runtime session missing for AgentSession {target.SessionId}. Reset the session to establish a new binding.",
                    "runtime_session_missing",
                    new { sessionId = target.SessionId, hint = "reset" });
            }

            return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
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

public sealed record AgentSessionFollowupResult(string SessionId, string Status = "sent");

public sealed record RunnerFollowupDeliveryResult(bool Accepted, string? Error = null);
