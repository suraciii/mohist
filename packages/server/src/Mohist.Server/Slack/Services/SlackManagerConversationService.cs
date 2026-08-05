using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

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
        return await ContinueAgentAsync(request, request.Message.Text, ct);
    }

    private async Task<SlackManagerConversationResult> ContinueAgentAsync(
        SlackManagerConversationRequest request,
        string prompt,
        CancellationToken ct)
    {
        var agent = await _agents.ResolveAsync(BuiltInAgentCatalog.MohistSlackName, ct)
            ?? throw new InvalidOperationException("The built-in mohist-slack Agent is not registered.");
        var sessionId = request.CurrentSessionId
            ?? await _dmSessions.GetCurrentSessionIdAsync(
                agent.ProjectId,
                request.Actor.EnrollmentId,
                request.Message.Identity.ConversationId,
                ct)
            ?? ManagerSessionId(request);
        var target = await _sessions.ResolveCanonicalFollowupTargetAsync(
            agent.ProjectId,
            sessionId,
            ct);
        if (target is null)
            return await LaunchSessionAsync(request, agent, prompt, sessionId, ct);

        var idempotencyKey = $"manager:{request.Message.Identity.AsKey()}";
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        try
        {
            var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: ManagerPrompt(prompt),
                Source: "slack-manager",
                IdempotencyKey: idempotencyKey,
                Provenance: Provenance(request)));
            await _followups.DispatchNextAsync(agent.ProjectId, sessionId, ct);
            return new SlackManagerConversationResult(
                Text: accepted.AlreadyAccepted ? "Manager request is already queued." : "Manager request accepted.",
                DispatchRef: $"manager:{sessionId}:{accepted.InputId}",
                SessionId: sessionId);
        }
        catch (RuntimeSessionMissingException)
        {
            return await LaunchSessionAsync(
                request,
                agent,
                prompt,
                ReplacementManagerSessionId(request),
                ct);
        }
        catch (SessionActivityUnknownException)
        {
            return Reply(request, "The Manager session state is uncertain. Please retry after it reconciles.");
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
            ManagerPrompt(prompt),
            new ConnectionLaunchOrigin(
                request.Actor.EnrollmentId,
                request.Actor.WorkspaceTeamId,
                request.Actor.SlackUserId,
                request.Message.Identity.ConversationId,
                request.Message.Identity.MessageTs,
                request.Message.ThreadTs),
            StartupContext(request.Actor),
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
            Text: "Manager request accepted.",
            DispatchRef: $"manager:{launch.SessionId}:{request.Message.Identity.MessageTs}",
            SessionId: launch.SessionId);
    }

    private static SlackManagerConversationResult Reply(
        SlackManagerConversationRequest request,
        string text) =>
        new(text, $"manager:{request.Message.Identity.AsKey()}:reply");

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

    private static AgentStartupContext StartupContext(ManagerActorContext actor) => new(
        $"Authenticated Manager actor for workspace {actor.WorkspaceTeamId}: {actor.SlackUserId}.",
        new AgentStartupContextProvenance("slack-manager", false, null));

    private static AgentSessionInputProvenance Provenance(SlackManagerConversationRequest request) => new(
        ProviderKind: "slack",
        WorkspaceId: request.Message.Identity.WorkspaceTeamId,
        ConversationId: request.Message.Identity.ConversationId,
        ThreadId: request.Message.ThreadTs,
        MemberId: request.Actor.SlackUserId,
        MessageId: request.Message.Identity.MessageTs,
        ConnectionId: request.Actor.EnrollmentId);

    private static string ManagerPrompt(string prompt) =>
        $"Manager request from the authenticated Slack actor. Treat this as a natural-language request. "
        + $"Use the server-authorized manager tool protocol only when you need authoritative data or a state change.\n\n{prompt.Trim()}";
}
