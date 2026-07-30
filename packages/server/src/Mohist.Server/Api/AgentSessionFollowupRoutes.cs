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
            AgentSessionFollowupDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var text = body?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return Rejected(sessionId, "followup_text_missing", "text is required");

            var project = context.GetResolvedProject();
            var idempotencyKey = AgentSessionRecoveryRoutes.RecoveryIdempotencyKey(context) ?? string.Empty;
            return await ExecuteFollowupAsync(project.Id, sessionId, text, idempotencyKey, sessions, grains, dispatcher, ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteFollowupAsync(
        string projectId,
        string sessionId,
        string text,
        string idempotencyKey,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher dispatcher,
        CancellationToken ct)
    {
        var target = await sessions.ResolveCanonicalFollowupTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return Rejected(sessionId, "not_found", $"Agent session {sessionId} not found");

        var grain = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        AgentSessionFollowupAcceptResult accept;
        try
        {
            accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: text,
                Source: "agent-session-followup",
                IdempotencyKey: idempotencyKey));
        }
        catch (RuntimeSessionMissingException ex)
        {
            return Rejected(ex.SessionId, "runtime_session_missing", ex.Message);
        }
        catch (RecoveryOperationInProgressException ex)
        {
            return Rejected(ex.SessionId, "recovery_in_progress", ex.Message);
        }
        catch (AgentSessionFollowupCapacityExceededException ex)
        {
            return Rejected(ex.SessionId, "capacity_exceeded", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Rejected(target.SessionId, "followup_rejected", ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                Status: "unknown",
                Error: ex.Message,
                Code: "followup_acceptance_unknown"));
        }
        catch (FollowupOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "followup_in_progress", new { sessionId = ex.SessionId });
        }
        catch (StopOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "stop_in_progress", new { sessionId = ex.SessionId, turnId = ex.TurnId });
        }
        catch (SessionActivityUnknownException ex)
        {
            return ApiResults.Conflict(ex.Message, "session_activity_unknown", new { sessionId = ex.SessionId });
        }
        catch (FollowupConcurrencyLimitException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "concurrency_limit",
                new { sessionId = ex.SessionId, agentId = ex.AgentId });
        }

        await dispatcher.DispatchNextAsync(projectId, target.SessionId, ct);

        return ApiResults.Ok(new AgentSessionFollowupResult(
            target.SessionId,
            InputId: accept.InputId,
            TurnId: accept.TurnId,
            Status: "accepted",
            InputAcceptance: AgentSessionObservationMapper.InputAcceptance(accept.InputAcceptance),
            TurnStatus: AgentSessionObservationMapper.TurnStatus(accept.TurnStatus)));
    }

    private static IResult Rejected(string sessionId, string code, string error) =>
        ApiResults.Ok(new AgentSessionFollowupResult(
            sessionId,
            Status: "rejected",
            Error: error,
            Code: code));
}

/// <summary>
/// Body for <c>POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup</c>.
/// <see cref="Text"/> is required and must be non-empty; whitespace-only text
/// is rejected with 400 (<c>followup_text_missing</c>) before any session
/// or runner lookup, mirroring the issue-scoped followup body shape.
/// </summary>
public sealed record GenericFollowupRequest(string? Text = null);

public sealed record AgentSessionFollowupResult(
    string? SessionId,
    string? InputId = null,
    string? TurnId = null,
    string Status = "accepted",
    string? Error = null,
    string? Code = null,
    string? InputAcceptance = null,
    string? TurnStatus = null);
