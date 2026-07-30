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
            return await ExecuteFollowupAsync(project.Id, sessionId, text, sessions, grains, runnerHub, connections, ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteFollowupAsync(
        string projectId,
        string sessionId,
        string text,
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
        AgentSessionFollowupReservation reservation;
        try
        {
            reservation = await grain.BeginFollowupAsync();
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
        catch (FollowupOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "followup_in_progress", new { sessionId = ex.SessionId });
        }
        catch (StopOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "stop_in_progress", new { sessionId = ex.SessionId, turnId = ex.TurnId });
        }

        if (string.IsNullOrWhiteSpace(target.RunnerId))
        {
            await AbandonReservationAsync(grain, reservation);
            return ApiResults.Fail("Runner is offline", 503, "runner_offline", new { runnerId = target.RunnerId });
        }

        var connectionId = connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            await AbandonReservationAsync(grain, reservation);
            return ApiResults.Fail("Runner is offline", 503, "runner_offline", new { runnerId = target.RunnerId });
        }

        if (string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId))
        {
            await AbandonReservationAsync(grain, reservation);
            var missing = new RuntimeSessionMissingException(target.SessionId, target.RuntimeSessionId, target.Runtime);
            return ApiResults.Conflict(missing.Message, "runtime_session_missing", new { sessionId = missing.SessionId });
        }

        string? inputId = null;
        string? turnId = null;
        if (reservation.StartsIdleTurn)
        {
            inputId = Guid.NewGuid().ToString("N");
            turnId = Guid.NewGuid().ToString("N");
            var source = string.Equals(target.SourceKind, "workflow", StringComparison.Ordinal)
                ? "workflow-followup"
                : "generic-followup";
            try
            {
                await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
                    InputId: inputId,
                    TurnId: turnId,
                    Prompt: text,
                    Source: source));
            }
            catch (InvalidOperationException ex)
            {
                await AbandonReservationAsync(grain, reservation);
                return ApiResults.Conflict(ex.Message, "followup_turn_conflict", new { sessionId = target.SessionId });
            }
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
        object payload = new
        {
            target = wireTarget,
            text,
            operationId = reservation.OperationId,
            inputId,
            turnId,
        };

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
            await AbandonReservationAsync(grain, reservation);
            return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
        }

        if (delivery?.Accepted == true)
        {
            if (reservation.OperationId is not null)
                await grain.ConfirmFollowupAsync(reservation.OperationId);
            return ApiResults.Ok(new AgentSessionFollowupResult(target.SessionId, InputId: inputId, TurnId: turnId));
        }

        await AbandonReservationAsync(grain, reservation);
        if (string.Equals(delivery?.Error, "missing", StringComparison.Ordinal))
        {
            if (inputId is not null && turnId is not null)
                await grain.AbandonFollowupTurnAsync(inputId, turnId);
            return ApiResults.Conflict(
                $"Runtime session missing for AgentSession {target.SessionId}.",
                "runtime_session_missing",
                new { sessionId = target.SessionId });
        }

        if (inputId is not null && turnId is not null)
            await grain.AbandonFollowupTurnAsync(inputId, turnId);
        return ApiResults.Fail("Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId });
    }

    private static Task AbandonReservationAsync(IAgentSessionGrain grain, AgentSessionFollowupReservation reservation) =>
        reservation.OperationId is null ? Task.CompletedTask : grain.AbandonFollowupAsync(reservation.OperationId);
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
    string Status = "sent",
    string? InputId = null,
    string? TurnId = null);

public sealed record RunnerFollowupDeliveryResult(bool Accepted, string? Error = null);
