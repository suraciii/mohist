using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Coordinates Manager messages through the ordinary Agent Session launch and
/// follow-up contracts. This service owns durable Session routing only; reply
/// text is produced by the Agent reply action.
/// </summary>
public sealed class SlackManagerConversationService : IScopedService, ISlackManagerConversationProcessor
{
    private readonly BuiltInAgentResolver _agents;
    private readonly IAgentLauncher _launcher;
    private readonly AgentSessionQuerier _sessions;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly IGrainFactory _grains;
    private readonly SlackDmSessionMappingStore _dmSessions;

    public SlackManagerConversationService(
        BuiltInAgentResolver agents,
        IAgentLauncher launcher,
        AgentSessionQuerier sessions,
        AgentSessionFollowupDispatcher followups,
        IGrainFactory grains,
        SlackDmSessionMappingStore dmSessions)
    {
        _agents = agents;
        _launcher = launcher;
        _sessions = sessions;
        _followups = followups;
        _grains = grains;
        _dmSessions = dmSessions;
    }

    public async Task<SlackManagerConversationResult> ProcessAsync(
        SlackManagerConversationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = request.Message.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return SlackManagerConversationResult.NotAccepted(request.CurrentSessionId);

        var agent = await _agents.ResolveAsync(BuiltInAgentCatalog.MohistSlackName, ct)
            ?? throw new InvalidOperationException("The built-in mohist-slack Agent is not registered.");
        var sessionId = request.CurrentSessionId
            ?? await _dmSessions.GetCurrentSessionIdAsync(
                BuiltInAgentCatalog.MohistSlackProjectId,
                request.Actor.EnrollmentId,
                request.Message.Identity.WorkspaceTeamId,
                request.Message.Identity.ConversationId,
                ct);

        if (string.IsNullOrWhiteSpace(sessionId))
            return await LaunchSessionAsync(request, agent, prompt, ManagerSessionId(request), ct);

        var target = await _sessions.ResolveCanonicalFollowupTargetAsync(
            agent.ProjectId,
            sessionId,
            ct);
        if (target is null)
            return await LaunchSessionAsync(
                request,
                agent,
                prompt,
                ReplacementManagerSessionId(request),
                ct);

        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var idempotencyKey = $"slack:{request.Message.Identity.AsKey()}";
        try
        {
            var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: prompt,
                Source: "agent-session-followup",
                IdempotencyKey: idempotencyKey,
                Provenance: Provenance(request)));
            await _followups.DispatchNextAsync(agent.ProjectId, sessionId, ct);
            return new SlackManagerConversationResult(
                SessionId: sessionId,
                DispatchRef: accepted.OperationId,
                InputId: accepted.InputId,
                TurnId: accepted.TurnId,
                Accepted: true);
        }
        catch (RuntimeSessionMissingException)
        {
            // The current message is the replacement Session's initial input.
            // Its message-derived Session id and the launch coordinator's
            // message-derived idempotency key make a retry converge to one
            // Session, input, and dispatch.
            return await LaunchSessionAsync(
                request,
                agent,
                prompt,
                ReplacementManagerSessionId(request),
                ct);
        }
        catch (SessionActivityUnknownException)
        {
            // Durable Session state remains authoritative. Do not manufacture
            // a Slack acknowledgement while the activity is unknown.
            return SlackManagerConversationResult.NotAccepted(sessionId);
        }
    }

    private async Task<SlackManagerConversationResult> LaunchSessionAsync(
        SlackManagerConversationRequest request,
        AgentInfo agent,
        string prompt,
        string sessionId,
        CancellationToken ct)
    {
        var launch = await _launcher.LaunchConnectionAsync(
            agent,
            prompt,
            new ConnectionLaunchOrigin(
                request.Actor.EnrollmentId,
                request.Actor.WorkspaceTeamId,
                request.Actor.SlackUserId,
                request.Message.Identity.ConversationId,
                request.Message.Identity.MessageTs,
                request.Message.ThreadTs),
            preMintedSessionId: sessionId,
            ct: ct);

        await _dmSessions.SetCurrentSessionIdAsync(
            agent.ProjectId,
            request.Actor.EnrollmentId,
            request.Message.Identity.WorkspaceTeamId,
            request.Actor.SlackUserId,
            request.Message.Identity.ConversationId,
            launch.SessionId,
            request.Message.Identity.MessageTs,
            ct);

        return new SlackManagerConversationResult(
            SessionId: launch.SessionId,
            DispatchRef: $"slack:{launch.SessionId}:{launch.InputId}",
            InputId: launch.InputId,
            TurnId: launch.TurnId,
            Accepted: true);
    }

    private static string ManagerSessionId(SlackManagerConversationRequest request) =>
        $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            request.Actor.EnrollmentId,
            request.Message.Identity.WorkspaceTeamId,
            request.Message.Identity.ConversationId))}";

    private static string ReplacementManagerSessionId(SlackManagerConversationRequest request) =>
        $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            request.Actor.EnrollmentId,
            request.Message.Identity.WorkspaceTeamId,
            request.Message.Identity.ConversationId,
            request.Message.Identity.MessageTs))}";

    private static AgentSessionInputProvenance Provenance(SlackManagerConversationRequest request) => new(
        ProviderKind: "slack",
        WorkspaceId: request.Message.Identity.WorkspaceTeamId,
        ConversationId: request.Message.Identity.ConversationId,
        ThreadId: request.Message.ThreadTs,
        MemberId: request.Actor.SlackUserId,
        MessageId: request.Message.Identity.MessageTs,
        ConnectionId: request.Actor.EnrollmentId,
        BoundThreadRootMessageId: request.Message.ThreadTs ?? request.Message.Identity.MessageTs);
}
