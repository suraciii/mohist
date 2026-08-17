using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

public sealed class SlackTurnControlService : IScopedService
{
    public const string StopActionId = "mohist_stop_turn";
    public const string RetryActionId = "mohist_retry_turn";
    public const string SelectionActionId = "mohist_select_connection";
    private static readonly TimeSpan StopActionLifetime = TimeSpan.FromMinutes(5);

    private readonly ISecretStore _secrets;
    private readonly IGrainFactory _grains;
    private readonly AgentSessionQuerier _sessions;
    private readonly SlackProviderInboxStore _inbox;
    private readonly ISessionStopDelivery _stopDelivery;
    private readonly TimeProvider _time;

    public SlackTurnControlService(
        ISecretStore secrets,
        IGrainFactory grains,
        AgentSessionQuerier sessions,
        SlackProviderInboxStore inbox,
        ISessionStopDelivery stopDelivery,
        TimeProvider time)
    {
        _secrets = secrets;
        _grains = grains;
        _sessions = sessions;
        _inbox = inbox;
        _stopDelivery = stopDelivery;
        _time = time;
    }

    public Task<SlackStopAction?> CreateStopActionAsync(
        AgentConnection connection,
        string sessionId,
        string turnId,
        string inputId,
        string dispatchRef,
        string actorSlackUserId,
        SlackMessageIdentity source,
        string? threadTs,
        CancellationToken ct = default) =>
        CreateStopActionAsync(
            connection,
            sessionId,
            turnId,
            inputId,
            dispatchRef,
            actorSlackUserId,
            source,
            threadTs,
            originalDirectMessage: false,
            ct);

    public async Task<SlackStopAction?> CreateStopActionAsync(
        AgentConnection connection,
        string sessionId,
        string turnId,
        string inputId,
        string dispatchRef,
        string actorSlackUserId,
        SlackMessageIdentity source,
        string? threadTs,
        bool originalDirectMessage,
        CancellationToken ct = default)
    {
        var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turn = await session.ResolveTurnControlAsync(turnId);
        if (turn?.Classification is not (AgentTurnControlClassification.Queued or AgentTurnControlClassification.Executing))
            return null;

        var initial = await session.GetInitialLaunchAsync();
        var provenance = initial?.Input?.Provenance;
        var initiator = provenance?.MemberId;
        if (!IsBoundToConnection(provenance, connection.Id)
            || !CanControl(connection, initiator, actorSlackUserId)
            || string.IsNullOrWhiteSpace(inputId)
            || string.IsNullOrWhiteSpace(dispatchRef))
            return null;

        var expiresAt = _time.GetUtcNow().Add(StopActionLifetime);
        var payload = new SlackStopActionPayload(
            Version: SlackActionCodec.Version,
            Action: SlackActionCodec.StopAction,
            ConnectionId: connection.Id,
            SessionId: sessionId,
            TurnId: turnId,
            InputId: inputId,
            DispatchRef: dispatchRef,
            ActorSlackUserId: actorSlackUserId,
            InitiatorSlackUserId: initiator!,
            ConversationId: source.ConversationId,
            MessageTs: source.MessageTs,
            ThreadTs: threadTs,
            Nonce: Guid.NewGuid().ToString("N"),
            ExpiresAt: expiresAt,
            Signature: null)
        {
            WorkspaceTeamId = source.WorkspaceTeamId,
            OriginalDirectMessage = originalDirectMessage,
        };
        var value = await CreateSignedActionValueAsync(connection, payload, ct);
        if (value is null)
            return null;

        return new SlackStopAction(StopActionId, value, expiresAt, BuildStopBlocks(value));
    }

    public Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        CancellationToken ct = default) =>
        HandleAsync(projectId, connection, request, interactionLeaseContext: null, ct);

    public async Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        SlackInteractionLeaseContext? interactionLeaseContext,
        CancellationToken ct = default)
    {
        if (!string.Equals(request.EventType, "block_actions", StringComparison.Ordinal)
            || !string.Equals(request.ActionId, StopActionId, StringComparison.Ordinal))
            return Rejected("unsupported_action", "This action is not supported.");

        var payload = await VerifySignedActionAsync<SlackStopActionPayload>(connection, request.ActionValue, ct);
        if (payload is null || !string.Equals(payload.Action, SlackActionCodec.StopAction, StringComparison.Ordinal))
            return Rejected("invalid_action", "This Stop action is invalid.");
        if (payload.ExpiresAt <= _time.GetUtcNow())
            return Rejected("expired", "This Stop action has expired.");
        if (!string.Equals(payload.ConnectionId, connection.Id, StringComparison.Ordinal)
            || !string.Equals(payload.WorkspaceTeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(request.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(payload.ConversationId, request.ConversationId, StringComparison.Ordinal)
            || !string.Equals(payload.ThreadTs, request.ThreadTs, StringComparison.Ordinal))
            return Rejected("stale_action", "This Stop action no longer matches the active Slack Connection.");
        if (!string.Equals(payload.ActorSlackUserId, request.ActorSlackUserId, StringComparison.Ordinal))
            return Rejected("unauthorized", "This Stop action belongs to a different Slack member.");

        var session = _grains.GetGrain<IAgentSessionGrain>(payload.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        var provenance = initial?.Input?.Provenance;
        var initiator = provenance?.MemberId;
        if (!IsBoundToConnection(provenance, connection.Id)
            || !string.Equals(initiator, payload.InitiatorSlackUserId, StringComparison.Ordinal)
            || !CanControl(connection, initiator, request.ActorSlackUserId))
            return Rejected("unauthorized", "Only the Connection Owner or the session initiator may stop this Turn.");

        var accepted = await _inbox.AcceptAsync(
            new SlackProviderInboxDraft(
                projectId,
                connection.Id,
                new SlackMessageIdentity(request.TeamId, request.ConversationId, $"action:{payload.Nonce}"),
                request.ActorSlackUserId,
                request.ThreadTs),
            new SlackProviderInboxRouteDraft(
                SlackProviderInboxRouteKinds.Stop,
                payload.SessionId,
                payload.TurnId),
            ct);
        if (accepted.AlreadyExisted)
            return Rejected("replayed", "This Stop action was already used.");

        var turn = await session.ResolveTurnControlAsync(payload.TurnId);
        var turnRecord = (await session.ListTurnsAsync()).SingleOrDefault(candidate =>
            string.Equals(candidate.Id, payload.TurnId, StringComparison.Ordinal));
        if (turn is null
            || turn.Classification is not (AgentTurnControlClassification.Queued or AgentTurnControlClassification.Executing)
            || turnRecord is null
            || !turnRecord.InputIds.Contains(payload.InputId, StringComparer.Ordinal))
        {
            await _inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            return Rejected("stale_action", "That Turn is no longer available.");
        }

        var target = await _sessions.ResolveStopTargetAsync(projectId, payload.SessionId, ct);
        if (target is null)
        {
            await _inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            return Rejected("stale_action", "That Turn is no longer available.");
        }

        var control = await AgentSessionStopOperations.StopAsync(
            projectId,
            _grains,
            _stopDelivery,
            target,
            payload.TurnId,
            ct);
        await _inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
        return control.Kind switch
        {
            TurnControlResultKind.Cancelled => Confirmed("cancelled", "Work cancelled."),
            TurnControlResultKind.Stopped => Confirmed("stopped", "Work stopped."),
            TurnControlResultKind.StopRequested => Confirmed("stop_requested", "Stop requested. Waiting for the runtime to confirm."),
            TurnControlResultKind.Unknown => Confirmed("unknown", "The runtime could not confirm whether work stopped."),
            TurnControlResultKind.NotCancellable => Confirmed("not_cancellable", "The runtime cannot stop this work."),
            TurnControlResultKind.RunnerUnavailable => Confirmed("runner_unavailable", "The runtime is unavailable; Stop was not confirmed."),
            TurnControlResultKind.Blocked => Confirmed("blocked", "Stop recovery deadline was exhausted."),
            _ => Rejected("stale_action", "That Turn is no longer executing."),
        };
    }

    public async Task<string?> CreateSignedActionValueAsync(
        AgentConnection connection,
        ISlackActionPayload payload,
        CancellationToken ct = default)
    {
        var key = await LoadSigningKeyAsync(connection, ct);
        if (key is null)
            return null;
        var signature = SlackActionCodec.Sign(payload, key);
        return SlackActionCodec.SerializeWithSignature(payload, signature);
    }

    public async Task<T?> VerifySignedActionAsync<T>(
        AgentConnection connection,
        string actionValue,
        CancellationToken ct = default)
        where T : class, ISlackActionPayload
    {
        var key = await LoadSigningKeyAsync(connection, ct);
        return key is not null
            && SlackActionCodec.TryVerify(actionValue, key, out T? payload)
            ? payload
            : null;
    }

    private async Task<byte[]?> LoadSigningKeyAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var token = await _secrets.LoadAsync(
                new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
            return token is { Length: > 0 } ? token : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsBoundToConnection(AgentSessionInputProvenance? provenance, string connectionId) =>
        string.Equals(provenance?.ProviderKind, "slack", StringComparison.Ordinal)
        && string.Equals(provenance?.ConnectionId, connectionId, StringComparison.Ordinal);

    private static bool CanControl(AgentConnection connection, string? initiator, string actorSlackUserId) =>
        string.Equals(connection.OwnerSlackUserId, actorSlackUserId, StringComparison.Ordinal)
        || string.Equals(initiator, actorSlackUserId, StringComparison.Ordinal);

    private static SlackTurnControlResult Rejected(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static SlackTurnControlResult Confirmed(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static JsonElement BuildStopBlocks(string value) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "actions",
                block_id = "mohist-turn-control",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Stop" },
                        style = "danger",
                        action_id = StopActionId,
                        value,
                    },
                },
            },
        });

    private static JsonElement BuildPresentationBlocks(string text) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text },
            },
        });
}

public sealed record SlackTurnControlResult(
    string State,
    string Text,
    JsonElement Blocks,
    string? ResultReference = null);
