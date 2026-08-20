using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public sealed class AgentSessionFollowupDispatcher : IScopedService
{
    private readonly AgentSessionQuerier _sessions;
    private readonly IGrainFactory _grains;
    private readonly IFollowupDeliveryDispatcher _delivery;

    public AgentSessionFollowupDispatcher(
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IFollowupDeliveryDispatcher delivery)
    {
        _sessions = sessions;
        _grains = grains;
        _delivery = delivery;
    }

    public async Task DispatchNextAsync(string projectId, string sessionId, CancellationToken ct)
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
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        if (dispatch is null)
            return;

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
                ExecutionSource: dispatch.ExecutionSource), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            throw;
        }
        catch
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            return;
        }
        if (!result.Accepted)
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
    }

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
