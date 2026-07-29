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
            var idempotencyKey = AgentSessionRecoveryRoutes.RecoveryIdempotencyKey(context) ?? string.Empty;
            return await ExecuteFollowupAsync(project.Id, sessionId, text, idempotencyKey, sessions, grains, runnerHub, connections, ct);
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
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
        CancellationToken ct)
    {
        var target = await sessions.ResolveCanonicalFollowupTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return ApiResults.NotFound($"Agent session {sessionId} not found");

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
            return ApiResults.Conflict(
                ex.Message,
                "runtime_session_missing",
                new { sessionId = ex.SessionId });
        }
        catch (RecoveryOperationInProgressException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "recovery_in_progress",
                new { sessionId = ex.SessionId, operation = ex.Operation });
        }
        catch (AgentSessionFollowupCapacityExceededException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "capacity_exceeded",
                new { sessionId = ex.SessionId, capacity = ex.Capacity });
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

        // accepted: persistence is the contract; runner offline does not
        // revert acceptance (the input stays accepted and the turn stays
        // queued; a same-key retry will re-attempt dispatch while queued).
        if (accept.AlreadyAccepted && !accept.ShouldRedeliver)
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                InputId: accept.InputId,
                TurnId: accept.TurnId,
                Status: "accepted"));
        }

        if (string.IsNullOrWhiteSpace(target.RunnerId))
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                InputId: accept.InputId,
                TurnId: accept.TurnId,
                Status: "accepted"));
        }

        var connectionId = connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId)
            || string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId))
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                InputId: accept.InputId,
                TurnId: accept.TurnId,
                Status: "accepted"));
        }

        object binding = new
        {
            runtime = target.Runtime,
            runtimeSessionId = target.RuntimeSessionId,
            runnerId = target.RunnerId,
            workDir = target.WorkDir,
        };
        object wireTarget = string.Equals(target.SourceKind, "workflow", StringComparison.Ordinal)
            ? new
            {
                kind = "workflow",
                projectId,
                workflowRunId = target.WorkflowRunId,
                sessionName = target.SessionName,
                binding,
            }
            : new
            {
                kind = "generic",
                projectId,
                sessionId = target.SessionId,
                definition = target.Definition,
                binding,
            };
        object payload = new { target = wireTarget, text, operationId = accept.OperationId };

        RunnerFollowupDeliveryResult? delivery;
        try
        {
            delivery = await runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerFollowupDeliveryResult?>(
                "ReceiveFollowup",
                payload,
                ct);
        }
        catch
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                InputId: accept.InputId,
                TurnId: accept.TurnId,
                Status: "accepted"));
        }

        if (delivery?.Accepted == true)
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                InputId: accept.InputId,
                TurnId: accept.TurnId,
                Status: "accepted"));
        }

        if (string.Equals(delivery?.Error, "missing", StringComparison.Ordinal))
        {
            return ApiResults.Conflict(
                $"Runtime session missing for AgentSession {target.SessionId}.",
                "runtime_session_missing",
                new { sessionId = target.SessionId });
        }

        return ApiResults.Ok(new AgentSessionFollowupResult(
            target.SessionId,
            InputId: accept.InputId,
            TurnId: accept.TurnId,
            Status: "accepted"));
    }
}

/// <summary>
/// Body for <c>POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup</c>.
/// <see cref="Text"/> is required and must be non-empty; whitespace-only text
/// is rejected with 400 (<c>followup_text_missing</c>) before any session
/// or runner lookup, mirroring the issue-scoped followup body shape.
/// </summary>
public sealed record GenericFollowupRequest(string? Text = null);

public sealed record AgentSessionFollowupResult(
    string SessionId,
    string? InputId = null,
    string? TurnId = null,
    string Status = "accepted");

public sealed record RunnerFollowupDeliveryResult(bool Accepted, string? Error = null);
