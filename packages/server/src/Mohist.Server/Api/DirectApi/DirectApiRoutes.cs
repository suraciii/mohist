using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The one registration of the direct external Agent API route group
/// (the public v1 HTTP surface): all seven v1 route templates
/// under <c>/api/v1/projects/{projectId}/…</c>, each carrying its
/// <see cref="RouteScopeRequirement"/> metadata — writes (launch,
/// follow-up, stop) require <c>operator</c>, reads (Job, Input, Turn,
/// Session events) accept <c>readonly</c> or <c>operator</c>.
/// <para>
/// The authorization pipeline in front of these routes —
/// <see cref="ExternalAgentApiMiddleware"/> pinned to
/// <see cref="ExternalAgentApiMiddleware.PathPrefix"/> — is the shipped
/// boundary and must not change as endpoint delegates land: carrier,
/// grant, scope, and Project authorization are settled before any
/// delegate runs.
/// </para>
/// <para>
/// The read delegates are served only from the persisted public
/// projection (the shared lag-checked read service). The Session event
/// stream uses the same projection checkpoint gate and its persisted
/// public journal; remaining write delegates replace their placeholders
/// in place without altering this registration's order or metadata.
/// </para>
/// </summary>
public static class DirectApiRoutes
{
    public static WebApplication MapDirectApiRoutes(this WebApplication app)
    {
        var group = app.MapGroup(
            ExternalAgentApiMiddleware.PathPrefix + "/projects/{" + ExternalAgentApiMiddleware.ProjectIdRouteName + "}");

        // Writes: operator only.
        group.MapPost("/agents/{agentId}/launch", async (
            HttpContext context,
            string projectId,
            string agentId,
            AgentQuerier agents,
            IAgentLauncher launcher,
            DirectApiIdempotencyService idempotency,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            await LaunchAsync(
                context,
                projectId,
                agentId,
                agents,
                launcher,
                idempotency,
                publicReads,
                ct))
            .RequireScopes(Scope.Operator);
        group.MapPost("/agent-sessions/{sessionId}/inputs", async (
            HttpContext context,
            string projectId,
            string sessionId,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            AgentSessionFollowupDispatcher dispatcher,
            DirectApiIdempotencyService idempotency,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            await FollowupAsync(
                context,
                projectId,
                sessionId,
                sessions,
                grains,
                dispatcher,
                idempotency,
                publicReads,
                ct))
            .RequireScopes(Scope.Operator);
        group.MapPost("/agent-turns/{turnId}/stop", async (
            HttpContext context,
            string projectId,
            string turnId,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            ISessionStopDelivery stopDelivery,
            DirectApiIdempotencyService idempotency,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            await DirectApiStopRoutes.ExecuteAsync(
                context,
                projectId,
                turnId,
                sessions,
                grains,
                stopDelivery,
                idempotency,
                publicReads,
                ct))
            .RequireScopes(Scope.Operator);

        // Reads: readonly or operator. Each read is served only from
        // the persisted public projection through the shared read
        // service: canonical Project membership answers the route's
        // 404 resource code, and a checkpoint that has not consumed
        // the anchor's durable source facts yet answers 503
        // projection_lag instead of a stale snapshot. The event route
        // applies the same gate before querying its public journal.
        group.MapGet("/agent-jobs/{jobId}", async (
            string projectId,
            string jobId,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            DirectApiResults.PublicRead(
                await publicReads.ReadJobAsync(projectId, jobId, ct),
                DirectApiErrorCodes.JobNotFound))
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-inputs/{inputId}", async (
            string projectId,
            string inputId,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            DirectApiResults.PublicRead(
                await publicReads.ReadInputAsync(projectId, inputId, ct),
                DirectApiErrorCodes.InputNotFound))
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-turns/{turnId}", async (
            string projectId,
            string turnId,
            PublicExecutionReadQuerier publicReads,
            CancellationToken ct) =>
            DirectApiResults.PublicRead(
                await publicReads.ReadTurnAsync(projectId, turnId, ct),
                DirectApiErrorCodes.TurnNotFound))
            .RequireScopes(Scope.Readonly, Scope.Operator);
        group.MapGet("/agent-sessions/{sessionId}/events", async (
            HttpContext context,
            string projectId,
            string sessionId,
            PublicSessionEventStreamQuerier eventStream,
            CancellationToken ct) =>
            await ReadEventsAsync(context, projectId, sessionId, eventStream, ct))
            .RequireScopes(Scope.Readonly, Scope.Operator);

        return app;
    }

    private static async Task<IResult> ReadEventsAsync(
        HttpContext context,
        string projectId,
        string sessionId,
        PublicSessionEventStreamQuerier eventStream,
        CancellationToken ct)
    {
        var query = context.Request.Query;
        string? after = null;
        if (query.TryGetValue("after", out var afterValues))
        {
            if (afterValues.Count != 1 || string.IsNullOrEmpty(afterValues[0]))
            {
                return DirectApiResults.CursorInvalid();
            }

            after = afterValues[0];
        }

        var limit = PublicSessionEventStreamQuerier.DefaultLimit;
        if (query.TryGetValue("limit", out var limitValues))
        {
            if (limitValues.Count != 1
                || !int.TryParse(
                    limitValues[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedLimit)
                || parsedLimit < 1)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status400BadRequest,
                    DirectApiErrorCodes.InvalidRequest,
                    "The limit query parameter must be a positive integer.");
            }

            limit = parsedLimit;
        }

        return DirectApiResults.PublicEvents(
            await eventStream.ReadAsync(projectId, sessionId, after, limit, ct));
    }

    private static async Task<IResult> FollowupAsync(
        HttpContext context,
        string projectId,
        string sessionId,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher dispatcher,
        DirectApiIdempotencyService idempotency,
        PublicExecutionReadQuerier publicReads,
        CancellationToken ct)
    {
        var key = DirectApiWriteValidation.ReadIdempotencyKey(context.Request.Headers);
        if (key.Disposition == IdempotencyKeyDisposition.Required)
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.IdempotencyKeyRequired,
                "The Idempotency-Key header is required.");
        }
        if (!key.IsValid)
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.IdempotencyKeyInvalid,
                "The Idempotency-Key header must contain 1 to 128 printable ASCII characters.");
        }

        var body = await DirectApiWriteValidation.ReadTextBodyAsync(context.Request.Body, ct);
        if (!body.IsValid)
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.InvalidRequest,
                "The request body must be a JSON object containing only a non-empty text string.");
        }

        var target = await sessions.ResolveCanonicalFollowupTargetAsync(projectId, sessionId, ct);
        if (target is null
            || !string.Equals(target.ProjectId, projectId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(target.AgentId))
        {
            return DirectApiResults.ResourceNotFound(DirectApiErrorCodes.SessionNotFound);
        }

        var publicKey = key.Value!;
        var text = body.Text!;
        var inputId = DirectApiWriteValidation.FollowupInputId(sessionId, publicKey);
        var turnId = DirectApiWriteValidation.FollowupTurnId(sessionId, publicKey);
        var fingerprint = DirectApiWriteValidation.FollowupFingerprint(sessionId, text);
        var scopeKey = DirectApiWriteValidation.FollowupScopeKey(sessionId, publicKey);
        var initialOutcome = new DirectApiFollowupOutcome(
            SessionId: sessionId,
            AgentId: target.AgentId,
            InputId: inputId,
            TurnId: turnId);
        var caller = context.Items[ExternalAgentCaller.HttpContextItemKey] as ExternalAgentCaller
            ?? throw new InvalidOperationException("The direct API caller was not resolved.");
        var claim = await idempotency.GetOrCreateAsync(
            DirectApiCommands.Followup,
            scopeKey,
            caller.CallerKeyId,
            fingerprint,
            turnId: null,
            JSON.Serialize(initialOutcome),
            ct);

        if (!string.Equals(claim.Mapping.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return DirectApiResults.Error(
                StatusCodes.Status409Conflict,
                DirectApiErrorCodes.IdempotencyKeyReused,
                "The Idempotency-Key has already been used for a different request.");
        }

        var outcome = DirectApiIdempotencyService.ReadOutcome<DirectApiFollowupOutcome>(claim.Mapping);
        if (claim.Mapping.State == DirectApiMappingStates.Pending)
        {
            try
            {
                var accepted = await grains.GetGrain<IAgentSessionGrain>(sessionId)
                    .AcceptFollowupAsync(new AcceptFollowupCommand(
                        Text: text,
                        Source: "agent-session-followup",
                        IdempotencyKey: publicKey,
                        PreMintedInputId: outcome.InputId,
                        PreMintedTurnId: outcome.TurnId));
                outcome = outcome with
                {
                    InputId = accepted.InputId,
                    TurnId = accepted.TurnId,
                };
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Followup,
                        scopeKey,
                        DirectApiMappingStates.Completed,
                        JSON.Serialize(outcome),
                        ct),
                };

                if (accepted.ShouldRedeliver)
                    await dispatcher.DispatchNextAsync(projectId, sessionId, ct);
            }
            catch (AgentSessionFollowupCapacityExceededException)
            {
                outcome = outcome with
                {
                    InputId = null,
                    TurnId = null,
                    RejectionCode = PublicExecutionFieldValues.Reasons.QueueFull,
                    RejectionReason = PublicExecutionFieldValues.Reasons.QueueFull,
                };
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Followup,
                        scopeKey,
                        DirectApiMappingStates.Rejected,
                        JSON.Serialize(outcome),
                        ct),
                };
            }
            catch (FollowupConcurrencyLimitException)
            {
                outcome = outcome with
                {
                    InputId = null,
                    TurnId = null,
                    RejectionCode = PublicExecutionFieldValues.Reasons.QueueFull,
                    RejectionReason = PublicExecutionFieldValues.Reasons.QueueFull,
                };
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Followup,
                        scopeKey,
                        DirectApiMappingStates.Rejected,
                        JSON.Serialize(outcome),
                        ct),
                };
            }
            catch (RuntimeSessionMissingException)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable,
                    DirectApiErrorCodes.FollowupPending,
                    "The follow-up is waiting for the canonical Session runtime; retry with the same Idempotency-Key.");
            }
            catch (RecoveryOperationInProgressException)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable,
                    DirectApiErrorCodes.FollowupPending,
                    "The follow-up is waiting for a Session recovery operation; retry with the same Idempotency-Key.");
            }
            catch (SessionActivityUnknownException)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable,
                    DirectApiErrorCodes.FollowupPending,
                    "The Session activity state is unresolved; retry with the same Idempotency-Key.");
            }
            catch (StopOperationInProgressException)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable,
                    DirectApiErrorCodes.FollowupPending,
                    "The Session is resolving another operation; retry with the same Idempotency-Key.");
            }
            catch (FollowupOperationInProgressException)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable,
                    DirectApiErrorCodes.FollowupPending,
                    "Another follow-up is being admitted; retry with the same Idempotency-Key.");
            }
            catch (InvalidOperationException)
            {
                outcome = outcome with
                {
                    InputId = null,
                    TurnId = null,
                    RejectionCode = DirectApiErrorCodes.FollowupRejected,
                    RejectionReason = DirectApiErrorCodes.FollowupRejected,
                };
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Followup,
                        scopeKey,
                        DirectApiMappingStates.Rejected,
                        JSON.Serialize(outcome),
                        ct),
                };
            }
        }

        outcome = DirectApiIdempotencyService.ReadOutcome<DirectApiFollowupOutcome>(claim.Mapping);
        if (claim.Mapping.State == DirectApiMappingStates.Rejected)
        {
            return DirectApiResults.Snapshot(
                DirectApiPublicObservation.RejectedFollowup(
                    projectId,
                    outcome,
                    claim.Mapping.CompletedAt ?? DateTimeOffset.UnixEpoch));
        }

        if (string.IsNullOrWhiteSpace(outcome.InputId))
        {
            return DirectApiResults.Error(
                StatusCodes.Status503ServiceUnavailable,
                DirectApiErrorCodes.FollowupPending,
                "The follow-up is still being admitted; retry with the same Idempotency-Key.");
        }

        var observation = await publicReads.ReadInputAsync(projectId, outcome.InputId, ct);
        return observation.Status == PublicReadStatus.NotFound
            ? DirectApiResults.ProjectionLag()
            : DirectApiResults.PublicRead(observation, DirectApiErrorCodes.InputNotFound);
    }

    private static async Task<IResult> LaunchAsync(
        HttpContext context,
        string projectId,
        string agentId,
        AgentQuerier agents,
        IAgentLauncher launcher,
        DirectApiIdempotencyService idempotency,
        PublicExecutionReadQuerier publicReads,
        CancellationToken ct)
    {
        var key = DirectApiWriteValidation.ReadIdempotencyKey(context.Request.Headers);
        if (key.Disposition == IdempotencyKeyDisposition.Required)
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.IdempotencyKeyRequired,
                "The Idempotency-Key header is required.");
        }
        if (!key.IsValid)
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.IdempotencyKeyInvalid,
                "The Idempotency-Key header must contain 1 to 128 printable ASCII characters.");
        }

        var body = await DirectApiWriteValidation.ReadTextBodyAsync(context.Request.Body, ct);
        if (!body.IsValid)
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.InvalidRequest,
                "The request body must be a JSON object containing only a non-empty text string.");
        }

        var agent = await agents.GetByIdAsync(projectId, agentId, ct);
        if (agent is null
            || string.Equals(agent.Status, AgentStatus.Archived, StringComparison.Ordinal))
        {
            return DirectApiResults.ResourceNotFound(DirectApiErrorCodes.AgentNotFound);
        }

        var publicKey = key.Value!;
        var text = body.Text!;
        var fingerprint = DirectApiWriteValidation.LaunchFingerprint(projectId, agent.Id, text);
        var scopeKey = DirectApiWriteValidation.LaunchScopeKey(projectId, agent.Id, publicKey);
        var coordinatorKey = DirectApiWriteValidation.DerivedLaunchCoordinatorKey(
            projectId,
            agent.Id,
            publicKey);
        var initialOutcome = new DirectApiLaunchOutcome(coordinatorKey);
        var caller = context.Items[ExternalAgentCaller.HttpContextItemKey] as ExternalAgentCaller
            ?? throw new InvalidOperationException("The direct API caller was not resolved.");
        var claim = await idempotency.GetOrCreateAsync(
            DirectApiCommands.Launch,
            scopeKey,
            caller.CallerKeyId,
            fingerprint,
            turnId: null,
            JSON.Serialize(initialOutcome),
            ct);

        if (!string.Equals(claim.Mapping.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return DirectApiResults.Error(
                StatusCodes.Status409Conflict,
                DirectApiErrorCodes.IdempotencyKeyReused,
                "The Idempotency-Key has already been used for a different request.");
        }

        var outcome = DirectApiIdempotencyService.ReadOutcome<DirectApiLaunchOutcome>(claim.Mapping);
        if (claim.Mapping.State == DirectApiMappingStates.Rejected)
            return RejectedLaunch(projectId, agent.Id, outcome, claim.Mapping.CompletedAt);

        if (claim.Mapping.State == DirectApiMappingStates.Pending)
        {
            var launchRequest = new AgentLaunchCoordinatorRequest(
                Prompt: text,
                AgentRef: agent.Id,
                Runtime: null,
                WorkspacePath: null,
                IssueNumber: null,
                EpicNumber: null,
                Repository: null,
                Title: null,
                ExactPromptFingerprint: true,
                Origin: "direct-api",
                TargetId: agent.Id);
            try
            {
                var result = claim.Created
                    ? null
                    : await launcher.ResumeIdempotentAsync(
                        projectId,
                        outcome.CoordinatorKey,
                        launchRequest,
                        ct);
                result ??= await launcher.LaunchIdempotentAsync(
                    agent,
                    text,
                    new AgentLaunchContext(
                        ProjectId: projectId,
                        Origin: "direct-api",
                        TargetId: agent.Id),
                    idempotencyKey: outcome.CoordinatorKey,
                    request: launchRequest,
                    ct: ct);
                outcome = outcome with
                {
                    JobId = result.JobKey,
                    SessionId = result.SessionId,
                    InputId = result.InputId,
                    TurnId = result.TurnId,
                };
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Launch,
                        scopeKey,
                        DirectApiMappingStates.Completed,
                        JSON.Serialize(outcome),
                        ct),
                };
            }
            catch (AgentReadinessException)
            {
                outcome = outcome with
                {
                    RejectionCode = DirectApiErrorCodes.AgentNotReady,
                    RejectionReason = "agent_not_ready",
                };
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Launch,
                        scopeKey,
                        DirectApiMappingStates.Rejected,
                        JSON.Serialize(outcome),
                        ct),
                };
            }
            catch (LaunchSetupPendingException)
            {
                return DirectApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable,
                    DirectApiErrorCodes.LaunchPending,
                    "The launch is still being admitted; retry with the same Idempotency-Key.");
            }
        }

        if (claim.Mapping.State == DirectApiMappingStates.Rejected)
            return RejectedLaunch(projectId, agent.Id, outcome, claim.Mapping.CompletedAt);

        if (string.IsNullOrWhiteSpace(outcome.JobId))
        {
            return DirectApiResults.Error(
                StatusCodes.Status503ServiceUnavailable,
                DirectApiErrorCodes.LaunchPending,
                "The launch is still being admitted; retry with the same Idempotency-Key.");
        }

        var observation = await publicReads.ReadJobAsync(projectId, outcome.JobId, ct);
        return observation.Status == PublicReadStatus.NotFound
            ? DirectApiResults.ProjectionLag()
            : DirectApiResults.PublicRead(observation, DirectApiErrorCodes.JobNotFound);
    }

    private static IResult RejectedLaunch(
        string projectId,
        string agentId,
        DirectApiLaunchOutcome outcome,
        DateTimeOffset? completedAt)
    {
        var observedAt = completedAt ?? DateTimeOffset.UnixEpoch;
        return DirectApiResults.Snapshot(
            DirectApiPublicObservation.Rejected(projectId, agentId, outcome, observedAt));
    }
}
