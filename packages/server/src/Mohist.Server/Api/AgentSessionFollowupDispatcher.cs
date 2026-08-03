using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Contracts;
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
                SlackExecutionContext: SlackExecutionContextFor(dispatch, target.SessionId)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
            throw;
        }
        if (!result.Accepted)
            await grain.ReleaseFollowupDispatchAsync(dispatch.OperationId);
    }

    private static AgentSlackExecutionContext? SlackExecutionContextFor(
        AgentSessionFollowupDispatch dispatch,
        string sessionId)
    {
        var provenance = dispatch.Provenance;
        return dispatch.InputId is null
            || provenance is null
            || !string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            ? null
            : SlackExecutionContextFactory.Create(
                provenance.WorkspaceId,
                provenance.ConversationId,
                provenance.ThreadId,
                provenance.MessageId,
                provenance.MemberId,
                provenance.ConnectionId ?? string.Empty,
                sessionId,
                dispatch.OperationId);
    }
}
