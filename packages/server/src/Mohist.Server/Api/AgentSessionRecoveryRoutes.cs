using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Canonical Compact and Reset routes for AgentSessions from either source.
/// Workflow-scoped aliases resolve their lookup keys to a stable session id
/// and delegate to the same command executors used by these routes.
/// Follow-up and stop already preserve that canonical AgentSession identity;
/// their existing runner transport remains outside recovery routing.
/// </summary>
public static class AgentSessionRecoveryRoutes
{
    public const string RecoveryPathPrefix = "/api/projects/{projectRef}/agent-sessions";

    public static WebApplication MapAgentSessionRecoveryRoutes(this WebApplication app)
    {
        var group = app.MapGroup(RecoveryPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/compact", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionResolver sessions,
            IGrainFactory grains,
            ISessionCommandDispatcher commands,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var canonicalSessionId = await sessions.ResolveCanonicalIdAsync(project.Id, sessionId, ct);
            if (canonicalSessionId is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            return await ExecuteCompactAsync(canonicalSessionId, RecoveryIdempotencyKey(context), grains, commands, ct);
        });

        group.MapPost("/{sessionId}/reset", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionResolver sessions,
            IGrainFactory grains,
            ISessionCommandDispatcher commands,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var canonicalSessionId = await sessions.ResolveCanonicalIdAsync(project.Id, sessionId, ct);
            if (canonicalSessionId is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            return await ExecuteResetAsync(
                canonicalSessionId,
                RecoveryIdempotencyKey(context),
                grains,
                commands,
                ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteCompactAsync(
        string sessionId,
        string? idempotencyKey,
        IGrainFactory grains,
        ISessionCommandDispatcher commands,
        CancellationToken ct)
    {
        var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
        SessionCommandRequest? request = null;
        try
        {
            if (await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, idempotencyKey) is { } completed)
                return ApiResults.Ok(completed);
            var processGeneration = await commands.GetCurrentProcessGenerationAsync((await grain.GetAsync())!.RunnerId!, ct);
            request = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, processGeneration, idempotencyKey);
            if (await grain.AdmitSessionCommandEffectAsync(request.OperationId, processGeneration)
                is not SessionCommandAdmissionOutcome.AdmittedNow)
                return CommandEffectAlreadyAdmitted(request);
            SessionCommandResult commandResult;
            try
            {
                commandResult = await commands.DispatchAsync(request, ct);
            }
            catch
            {
                await grain.AbandonResetAsync(request.OperationId);
                throw;
            }
            if (MapCommandResult(request, commandResult) is { } commandFailure)
            {
                await grain.AbandonResetAsync(request.OperationId);
                return commandFailure;
            }

            if (!await commands.IsCurrentProcessGenerationAsync(request.RunnerId, request.ProcessGeneration, ct))
                return CommandEffectAlreadyAdmitted(request);
            var result = await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(
                request.OperationId,
                OwnerProcessGeneration: request.ProcessGeneration));
            return ApiResults.Ok(result);
        }
        catch (RuntimeSessionMissingException ex)
        {
            return RuntimeSessionMissingResult(ex);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("currently active", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResults.Conflict(ex.Message, "session_active", new { sessionId });
        }
        catch (RecoveryOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "recovery_in_progress", new { sessionId = ex.SessionId, operation = ex.Operation });
        }
        catch
        {
            throw;
        }
    }

    internal static async Task<IResult> ExecuteResetAsync(
        string sessionId,
        string? idempotencyKey,
        IGrainFactory grains,
        ISessionCommandDispatcher commands,
        CancellationToken ct)
    {
        var grain = grains.GetGrain<IAgentSessionGrain>(sessionId);
        SessionCommandRequest? request = null;
        try
        {
            if (await grain.GetCompletedRecoveryAsync(SessionCommandKind.Reset, idempotencyKey) is { } completed)
                return ApiResults.Ok(completed);
            var processGeneration = await commands.GetCurrentProcessGenerationAsync((await grain.GetAsync())!.RunnerId!, ct);
            request = await grain.BeginResetAsync(processGeneration, idempotencyKey);
            if (await grain.AdmitSessionCommandEffectAsync(request.OperationId, processGeneration)
                is not SessionCommandAdmissionOutcome.AdmittedNow)
                return CommandEffectAlreadyAdmitted(request);
            SessionCommandResult commandResult;
            try
            {
                commandResult = await commands.DispatchAsync(request, ct);
            }
            catch
            {
                await grain.AbandonResetAsync(request.OperationId);
                throw;
            }
            if (MapCommandResult(request, commandResult) is { } commandFailure)
            {
                await grain.AbandonResetAsync(request.OperationId);
                return commandFailure;
            }

            if (!await commands.IsCurrentProcessGenerationAsync(request.RunnerId, request.ProcessGeneration, ct))
                return CommandEffectAlreadyAdmitted(request);
            var result = await grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
                request.OperationId,
                commandResult.RuntimeSessionId!,
                request.Runtime,
                request.ProcessGeneration));
            return ApiResults.Ok(result);
        }
        catch (RuntimeSessionMissingException ex)
        {
            return RuntimeSessionMissingResult(ex);
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
        catch (RecoveryOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "recovery_in_progress", new { sessionId = ex.SessionId, operation = ex.Operation });
        }
        catch
        {
            throw;
        }
    }

    private static IResult CommandEffectAlreadyAdmitted(SessionCommandRequest request) =>
        ApiResults.Fail(
            "Runner command outcome is unavailable after its effect was admitted",
            503,
            "runner_unavailable",
            new { sessionId = request.SessionId, runnerId = request.RunnerId });

    private static IResult? MapCommandResult(
        SessionCommandRequest request,
        SessionCommandResult result)
    {
        if (result.Ok)
        {
            var valid = result.Error is null
                && (request.Command switch
                {
                    SessionCommandKind.Compact => result.RuntimeSessionId is null,
                    SessionCommandKind.Reset => !string.IsNullOrWhiteSpace(result.RuntimeSessionId)
                        && !string.Equals(result.RuntimeSessionId, request.RuntimeSessionId, StringComparison.Ordinal),
                    _ => false,
                });
            return valid ? null : InvalidRunnerResult(request.SessionId);
        }

        if (result.RuntimeSessionId is not null || result.Error is null)
            return InvalidRunnerResult(request.SessionId);

        return result.Error switch
        {
            SessionCommandError.Conflict => ApiResults.Conflict(
                $"AgentSession {request.SessionId} is currently active; Compact and Reset require an idle session.",
                "session_active",
                new { sessionId = request.SessionId }),
            SessionCommandError.Missing => RuntimeSessionMissingResult(new RuntimeSessionMissingException(
                request.SessionId,
                request.RuntimeSessionId,
                request.Runtime)),
            SessionCommandError.NotStarted => ApiResults.Fail(
                "Runner did not start the session command",
                503,
                "runner_command_not_started",
                new { sessionId = request.SessionId, runnerId = request.RunnerId }),
            SessionCommandError.Unavailable => ApiResults.Fail(
                "Runner is unavailable",
                503,
                "runner_unavailable",
                new { sessionId = request.SessionId, runnerId = request.RunnerId }),
            _ => InvalidRunnerResult(request.SessionId),
        };
    }

    private static IResult RuntimeSessionMissingResult(RuntimeSessionMissingException ex) =>
        ApiResults.Conflict(
            ex.Message,
            "runtime_session_missing",
            new { sessionId = ex.SessionId });

    private static IResult InvalidRunnerResult(string sessionId) =>
        ApiResults.Fail(
            "Runner returned an invalid SessionCommand result",
            502,
            "runner_invalid_response",
            new { sessionId });

    internal static string? RecoveryIdempotencyKey(HttpContext context) =>
        context.Request.Headers.TryGetValue("Idempotency-Key", out var values)
            ? values.FirstOrDefault()
            : null;
}
