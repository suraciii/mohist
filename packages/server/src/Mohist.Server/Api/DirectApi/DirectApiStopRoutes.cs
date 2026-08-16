using Microsoft.AspNetCore.Http;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.DirectApi;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api.DirectApi;

internal static class DirectApiStopRoutes
{
    public static async Task<IResult> ExecuteAsync(
        HttpContext context,
        string projectId,
        string turnId,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        ISessionStopDelivery stopDelivery,
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

        if (!await IsEmptyBodyAsync(context.Request.Body, ct))
        {
            return DirectApiResults.Error(
                StatusCodes.Status400BadRequest,
                DirectApiErrorCodes.InvalidRequest,
                "The stop request body must be empty.");
        }

        var caller = context.Items[ExternalAgentCaller.HttpContextItemKey] as ExternalAgentCaller
            ?? throw new InvalidOperationException("The direct API caller was not resolved.");
        var canonical = await sessions.ResolveCanonicalTurnStopTargetAsync(projectId, turnId, ct);
        if (canonical is null)
            return DirectApiResults.ResourceNotFound(DirectApiErrorCodes.TurnNotFound);

        var publicKey = key.Value!;
        var scopeKey = DirectApiWriteValidation.StopScopeKey(
            turnId,
            caller.CallerKeyId,
            publicKey);
        var fingerprint = DirectApiWriteValidation.StopFingerprint(turnId);
        var operationId = AgentLaunchCoordinatorCodec.StableToken(
            $"direct-api|stop|{projectId}|{canonical.SessionId}|{turnId}|{caller.CallerKeyId}|{publicKey}");
        var initialOutcome = new DirectApiStopOutcome(
            projectId,
            canonical.SessionId,
            turnId,
            operationId);
        var session = grains.GetGrain<IAgentSessionGrain>(canonical.SessionId);

        await ReconcileSettledPendingStopAsync(
            turnId,
            session,
            idempotency,
            ct);

        var claim = await idempotency.GetOrCreateAsync(
            DirectApiCommands.Stop,
            scopeKey,
            caller.CallerKeyId,
            fingerprint,
            turnId,
            JSON.Serialize(initialOutcome),
            ct);

        if (claim.StopOutcomeUnknown)
        {
            return DirectApiResults.Error(
                StatusCodes.Status409Conflict,
                DirectApiErrorCodes.StopOutcomeUnknown,
                "Another stop for this Turn has an unresolved outcome.");
        }

        if (!string.Equals(claim.Mapping.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return DirectApiResults.Error(
                StatusCodes.Status409Conflict,
                DirectApiErrorCodes.IdempotencyKeyReused,
                "The Idempotency-Key has already been used for a different request.");
        }

        var outcome = DirectApiIdempotencyService.ReadOutcome<DirectApiStopOutcome>(claim.Mapping);
        if (claim.Mapping.State == DirectApiMappingStates.Pending)
        {
            var frozen = ReadFrozenTarget(claim.Mapping);
            if (frozen is null)
            {
                var prepared = await PrepareFrozenTargetAsync(
                    canonical,
                    session,
                    operationId,
                    ct);
                frozen = prepared.Target;
                claim = claim with
                {
                    Mapping = await idempotency.FreezeStopTargetAsync(
                        scopeKey,
                        JSON.Serialize(frozen),
                        ct),
                };
            }

            if (!claim.Created
                && await HasMatchingCanonicalStopClaimAsync(session, frozen))
            {
                return await ReadTurnObservationAsync(projectId, turnId, publicReads, ct);
            }

            var result = await AgentSessionStopOperations.StopAsync(
                projectId,
                grains,
                stopDelivery,
                ToSessionStopTarget(frozen),
                turnId,
                ct,
                frozen.OperationId);

            if (AgentSessionStopResultPolicy.CompletesDirectMapping(result))
            {
                claim = claim with
                {
                    Mapping = await idempotency.CompleteAsync(
                        DirectApiCommands.Stop,
                        scopeKey,
                        DirectApiMappingStates.Completed,
                        JSON.Serialize(outcome),
                        ct),
                };
            }
            else
            {
                // The durable mapping stays pending until the fenced stop
                // lifecycle confirms an outcome. Do not let a caught-up
                // projection turn that unresolved command into a misleading
                // successful response.
                return DirectApiResults.StopPending();
            }
        }

        return await ReadTurnObservationAsync(projectId, turnId, publicReads, ct);
    }

    private static async Task<(DirectApiFrozenStopTarget Target, AgentTurnControlState? Control)> PrepareFrozenTargetAsync(
        CanonicalTurnStopTarget canonical,
        IAgentSessionGrain session,
        string operationId,
        CancellationToken ct)
    {
        _ = ct;
        var control = await session.ResolveTurnControlAsync(canonical.TurnId);
        var deadline = (DateTimeOffset?)null;
        var frozenOperationId = operationId;
        if (control?.Classification == AgentTurnControlClassification.Executing)
        {
            var claim = await session.ClaimTurnStopAsync(canonical.TurnId, operationId);
            frozenOperationId = claim.OperationId ?? operationId;
            deadline = (await session.GetStopClaimAsync())?.DeadlineAt;
        }

        return (
            new DirectApiFrozenStopTarget(
                canonical.ProjectId,
                canonical.SessionId,
                canonical.TurnId,
                canonical.TurnRevision,
                canonical.ContextGeneration,
                new DirectApiFrozenStopBinding(
                    canonical.DeliveryTarget.RunnerId,
                    canonical.DeliveryTarget.SourceKind,
                    canonical.DeliveryTarget.WorkflowRunId,
                    canonical.DeliveryTarget.SessionName,
                    canonical.DeliveryTarget.Runtime,
                    canonical.DeliveryTarget.RuntimeSessionId,
                    canonical.DeliveryTarget.WorkDir),
                deadline,
                frozenOperationId),
            control);
    }

    private static async Task ReconcileSettledPendingStopAsync(
        string turnId,
        IAgentSessionGrain session,
        DirectApiIdempotencyService idempotency,
        CancellationToken ct)
    {
        var pending = await idempotency.FindPendingStopAsync(turnId, ct);
        if (pending is null || string.IsNullOrWhiteSpace(pending.FrozenTarget))
            return;

        var control = await session.ResolveTurnControlAsync(turnId);
        var stopClaim = await session.GetStopClaimAsync();
        if (!IsSettled(control, stopClaim))
            return;

        await idempotency.CompleteAsync(
            DirectApiCommands.Stop,
            pending.ScopeKey,
            DirectApiMappingStates.Completed,
            pending.Outcome
                ?? throw new InvalidOperationException("The pending direct API stop mapping has no outcome."),
            ct);
    }

    private static bool IsSettled(
        AgentTurnControlState? control,
        AgentSessionStopClaim? stopClaim)
    {
        // A later confirmed terminal Turn is the lifecycle fence. It can
        // resolve an older unknown stop claim even when that claim's marker
        // was already settled as Unknown.
        if (control?.Classification == AgentTurnControlClassification.Terminal)
            return control.Status != AgentTurnStatus.Unknown;
        if (stopClaim?.IsActive == true)
            return false;
        if (stopClaim?.Disposition is AgentSessionStopDisposition.Unknown
            or AgentSessionStopDisposition.Blocked)
            return false;
        if (control is null)
            return false;
        return stopClaim?.Disposition is AgentSessionStopDisposition.Stopped
            or AgentSessionStopDisposition.NotCancellable
            or AgentSessionStopDisposition.Ended
            or AgentSessionStopDisposition.Idle;
    }

    private static DirectApiFrozenStopTarget? ReadFrozenTarget(
        DirectApiIdempotencyMappingRow mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.FrozenTarget))
            return null;
        try
        {
            return JSON.Deserialize<DirectApiFrozenStopTarget>(mapping.FrozenTarget);
        }
        catch (Exception) when (mapping.State == DirectApiMappingStates.Pending)
        {
            return null;
        }
    }

    private static async Task<bool> HasMatchingCanonicalStopClaimAsync(
        IAgentSessionGrain session,
        DirectApiFrozenStopTarget frozen)
    {
        var claim = await session.GetStopClaimAsync();
        return claim is not null
            && string.Equals(claim.TurnId, frozen.TurnId, StringComparison.Ordinal)
            && string.Equals(claim.OperationId, frozen.OperationId, StringComparison.Ordinal);
    }

    private static SessionStopTarget ToSessionStopTarget(DirectApiFrozenStopTarget frozen) =>
        new(
            frozen.Binding.RunnerId ?? string.Empty,
            frozen.SessionId,
            frozen.Binding.SourceKind ?? string.Empty,
            frozen.Binding.WorkflowRunId,
            frozen.Binding.SessionName,
            frozen.Binding.Runtime,
            frozen.Binding.RuntimeSessionId,
            frozen.Binding.WorkDir);

    private static async Task<IResult> ReadTurnObservationAsync(
        string projectId,
        string turnId,
        PublicExecutionReadQuerier publicReads,
        CancellationToken ct)
    {
        var observation = await publicReads.ReadTurnAsync(projectId, turnId, ct);
        return observation.Status == PublicReadStatus.NotFound
            ? DirectApiResults.ProjectionLag()
            : DirectApiResults.PublicRead(observation, DirectApiErrorCodes.TurnNotFound);
    }

    private static async Task<bool> IsEmptyBodyAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[1];
        return await body.ReadAsync(buffer.AsMemory(), ct) == 0;
    }
}
