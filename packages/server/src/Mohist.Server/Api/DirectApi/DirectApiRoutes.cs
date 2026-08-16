using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Infrastructure.PublicApi;

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
/// projection (the shared lag-checked read service). The write and
/// event-stream templates still answer with the placeholder result
/// until their endpoint delegates land; they replace it in place,
/// without altering this registration's order or metadata.
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
        group.MapPost("/agent-sessions/{sessionId}/inputs", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Operator);
        group.MapPost("/agent-turns/{turnId}/stop", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Operator);

        // Reads: readonly or operator. Each read is served only from
        // the persisted public projection through the shared read
        // service: canonical Project membership answers the route's
        // 404 resource code, and a checkpoint that has not consumed
        // the anchor's durable source facts yet answers 503
        // projection_lag instead of a stale snapshot.
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
        group.MapGet("/agent-sessions/{sessionId}/events", () => DirectApiResults.NotImplemented())
            .RequireScopes(Scope.Readonly, Scope.Operator);

        return app;
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
        var scopeKey = $"{projectId}|{agent.Id}|{publicKey}";
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

        return DirectApiResults.PublicRead(
            await publicReads.ReadJobAsync(projectId, outcome.JobId, ct),
            DirectApiErrorCodes.JobNotFound);
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
