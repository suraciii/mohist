using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public sealed class AgentSessionFollowupDispatcher : IScopedService
{
    private readonly AgentSessionQuerier _sessions;
    private readonly IGrainFactory _grains;
    private readonly IFollowupDeliveryDispatcher _delivery;
    private readonly ManagerExecutionCapabilityIssuer _managerCredentials;
    private readonly ManagerActorAccessDecider _managerActors;
    private readonly TimeProvider _timeProvider;

    public AgentSessionFollowupDispatcher(
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IFollowupDeliveryDispatcher delivery,
        ManagerExecutionCapabilityIssuer managerCredentials,
        ManagerActorAccessDecider managerActors,
        TimeProvider timeProvider)
    {
        _sessions = sessions;
        _grains = grains;
        _delivery = delivery;
        _managerCredentials = managerCredentials;
        _managerActors = managerActors;
        _timeProvider = timeProvider;
    }

    public Task DispatchNextAsync(string projectId, string sessionId, CancellationToken ct) =>
        DispatchAsync(projectId, sessionId, targetTurnId: null, ct);

    public Task DispatchForTurnAsync(string projectId, string sessionId, string turnId, CancellationToken ct) =>
        DispatchAsync(projectId, sessionId, turnId, ct);

    private async Task DispatchAsync(string projectId, string sessionId, string? targetTurnId, CancellationToken ct)
    {
        var target = await _sessions.ResolveCanonicalFollowupTargetAsync(projectId, sessionId, ct);
        if (target is null || string.IsNullOrWhiteSpace(target.RunnerId)
            || string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId))
            return;

        // Every newly emitted follow-up carries the v1 source marker. Hold
        // it when the selected Runner has not advertised the matching wire
        // contract; legacy source-less work remains the Runner's compatibility
        // concern and is never relabeled here.
        var runnerInfo = await _grains.GetGrain<IRunnerGrain>(target.RunnerId).GetInfoAsync();
        if (runnerInfo is null
            || !runnerInfo.Capabilities.Any(capability =>
                string.Equals(capability, AgentExecutionSources.Version1Capability, StringComparison.Ordinal)
                || string.Equals(capability, "spec/*", StringComparison.Ordinal)))
            return;

        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var dispatch = targetTurnId is null
            ? await grain.BeginNextFollowupDispatchAsync()
            : await grain.BeginFollowupDispatchForTurnAsync(targetTurnId);
        if (dispatch is null)
            return;

        var managerGrant = await IssueManagerGrantAsync(target, dispatch, ct);
        if (managerGrant is ManagerGrantResult { Authorized: false })
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            return;
        }

        FollowupDeliveryResult result;
        try
        {
            result = await _delivery.DispatchAsync(new FollowupDeliveryRequest(
                ProjectId: projectId,
                SessionId: target.SessionId,
                SourceKind: target.SourceKind,
                WorkflowRunId: target.WorkflowRunId,
                SessionName: target.SessionName,
                RunnerId: target.RunnerId,
                Runtime: target.Runtime,
                RuntimeSessionId: target.RuntimeSessionId,
                WorkDir: target.WorkDir,
                Definition: target.Definition,
                OperationId: dispatch.OperationId,
                InputTexts: dispatch.InputTexts,
                Attachments: dispatch.Attachments,
                InputId: dispatch.InputId,
                SlackExecutionContext: SlackExecutionContextFor(projectId, dispatch, target.SessionId),
                TurnId: dispatch.TurnId,
                ExecutionSource: dispatch.ExecutionSource,
                ManagerExecutionGrant: managerGrant?.Grant,
                OriginMarker: AgentOriginMarkers.IsManager(target.ProjectId)
                    ? AgentOriginMarkers.SlackManager
                    : null), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            RevokeManagerGrant(managerGrant);
            throw;
        }
        catch
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            RevokeManagerGrant(managerGrant);
            return;
        }
        if (!result.Accepted)
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            RevokeManagerGrant(managerGrant);
        }
    }

    private async Task<ManagerGrantResult?> IssueManagerGrantAsync(
        CanonicalFollowupTarget target,
        AgentSessionFollowupDispatch dispatch,
        CancellationToken ct)
    {
        if (!string.Equals(target.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal))
            return null;

        var provenance = dispatch.Provenance;
        var context = provenance is null
            ? null
            : SlackExecutionContextFor(target.ProjectId!, dispatch, target.SessionId);
        var anchor = context?.ReplyAnchor;
        if (anchor is null
            || !string.Equals(anchor.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
            || !string.Equals(anchor.OwnerKind, SlackDeliveryOwnerKinds.Manager, StringComparison.Ordinal))
            return new ManagerGrantResult(false, null);

        var authentication = await _managerActors.AuthenticateAsync(
            anchor.WorkspaceId,
            anchor.InitiatingMemberId,
            ct);
        if (!authentication.Allowed
            || authentication.Actor is null
            || !string.Equals(authentication.Actor.EnrollmentId, anchor.ConnectionId, StringComparison.Ordinal))
            return new ManagerGrantResult(false, null);

        var authorization = await _managerActors.AuthorizeAsync(authentication.Actor, ct: ct);
        if (!authorization.Allowed)
            return new ManagerGrantResult(false, null);

        var origin = new ManagerExecutionOrigin(
            anchor.WorkspaceId,
            anchor.ConversationId,
            anchor.ThreadRootMessageId,
            anchor.TriggeringMessageId,
            anchor.InitiatingMemberId,
            anchor.ConnectionId,
            anchor.SessionId,
            anchor.DispatchRef);
        var grant = _managerCredentials.Issue(new ManagerExecutionIssueRequest(
            ExecutionId: $"manager:{target.SessionId}:{dispatch.OperationId}",
            Origin: origin,
            Now: _timeProvider.GetUtcNow(),
            Lifetime: ManagerExecutionCapabilityIssuer.DefaultLifetime));
        return new ManagerGrantResult(true, grant);
    }

    private void RevokeManagerGrant(ManagerGrantResult? managerGrant)
    {
        if (managerGrant?.Grant is { } grant)
            _managerCredentials.RevokeExecution(grant.ExecutionId);
    }

    private sealed record ManagerGrantResult(bool Authorized, ManagerExecutionGrant? Grant);

    private static AgentSlackExecutionContext? SlackExecutionContextFor(
        string projectId,
        AgentSessionFollowupDispatch dispatch,
        string sessionId)
    {
        if (string.Equals(dispatch.ExecutionSource, AgentExecutionSources.NonSlack, StringComparison.Ordinal))
            return null;
        if (!string.Equals(dispatch.ExecutionSource, AgentExecutionSources.Slack, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown follow-up execution source '{dispatch.ExecutionSource}'.");

        var provenance = dispatch.Provenance
            ?? throw new InvalidOperationException("Slack follow-up dispatch has no representative provenance.");
        if (!string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(provenance.BoundThreadRootMessageId))
            throw new InvalidOperationException("Slack follow-up dispatch has incomplete durable provenance.");

        return SlackExecutionContextFactory.Create(
            provenance.WorkspaceId,
            provenance.ConversationId,
            provenance.BoundThreadRootMessageId,
            provenance.MessageId,
            provenance.MemberId,
            provenance.ConnectionId ?? string.Empty,
            sessionId,
            dispatch.OperationId,
            projectId: string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                ? SlackDeliveryOwnerIds.ManagerProjectId
                : null,
            ownerKind: string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                ? SlackDeliveryOwnerKinds.Manager
                : null);
    }
}
