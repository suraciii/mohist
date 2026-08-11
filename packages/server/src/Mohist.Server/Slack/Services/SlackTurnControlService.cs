using System.Security.Cryptography;
using System.Text;
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
    private const string StopAction = "stop";
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

    public async Task<SlackStopAction?> CreateStopActionAsync(
        AgentConnection connection,
        string sessionId,
        string turnId,
        string inputId,
        string dispatchRef,
        string actorSlackUserId,
        SlackMessageIdentity source,
        string? threadTs,
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
            Version: "v1",
            Action: StopAction,
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
            Signature: null);
        var signature = await TrySignAsync(connection, payload, ct);
        if (signature is null)
            return null;

        var value = JSON.Serialize(payload with { Signature = signature });
        return new SlackStopAction(StopActionId, value, expiresAt, BuildStopBlocks(value));
    }

    public async Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        CancellationToken ct = default)
    {
        if (!string.Equals(request.EventType, "block_actions", StringComparison.Ordinal)
            || !string.Equals(request.ActionId, StopActionId, StringComparison.Ordinal))
            return Rejected("unsupported_action", "This action is not supported.");

        var payload = await VerifyAsync(connection, request.ActionValue, ct);
        if (payload is null)
            return Rejected("invalid_action", "This Stop action is invalid.");
        if (payload.ExpiresAt <= _time.GetUtcNow())
            return Rejected("expired", "This Stop action has expired.");
        if (!string.Equals(payload.ConnectionId, connection.Id, StringComparison.Ordinal)
            || !string.Equals(request.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(payload.ConversationId, request.ConversationId, StringComparison.Ordinal))
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

    private async Task<string?> TrySignAsync(
        AgentConnection connection,
        SlackStopActionPayload payload,
        CancellationToken ct)
    {
        var key = await LoadSigningKeyAsync(connection, ct);
        return key is null
            ? null
            : Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(payload))));
    }

    private async Task<SlackStopActionPayload?> VerifyAsync(
        AgentConnection connection,
        string actionValue,
        CancellationToken ct)
    {
        SlackStopActionPayload? payload;
        try
        {
            payload = JSON.Deserialize<SlackStopActionPayload>(actionValue);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || !string.Equals(payload.Version, "v1", StringComparison.Ordinal)
            || !string.Equals(payload.Action, StopAction, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.Signature)
            || string.IsNullOrWhiteSpace(payload.Nonce))
            return null;

        var expected = await TrySignAsync(connection, payload with { Signature = null }, ct);
        return expected is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(payload.Signature))
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

    private static string Canonical(SlackStopActionPayload payload) => string.Join(
        "\n",
        payload.Version,
        payload.Action,
        payload.ConnectionId,
        payload.SessionId,
        payload.TurnId,
        payload.InputId,
        payload.DispatchRef,
        payload.ActorSlackUserId,
        payload.InitiatorSlackUserId,
        payload.ConversationId,
        payload.MessageTs,
        payload.ThreadTs ?? string.Empty,
        payload.Nonce,
        payload.ExpiresAt.ToUnixTimeMilliseconds());

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

public sealed record SlackStopAction(
    string ActionId,
    string ActionValue,
    DateTimeOffset ExpiresAt,
    JsonElement Blocks);

public sealed record SlackTurnControlResult(string State, string Text, JsonElement Blocks);

public sealed record SlackStopActionPayload(
    string Version,
    string Action,
    string ConnectionId,
    string SessionId,
    string TurnId,
    string InputId,
    string DispatchRef,
    string ActorSlackUserId,
    string InitiatorSlackUserId,
    string ConversationId,
    string MessageTs,
    string? ThreadTs,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string? Signature);
