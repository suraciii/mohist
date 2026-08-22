using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Slack;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

public sealed class SlackRetryActionService : IScopedService
{
    public const string RetryActionId = "mohist_retry_turn";
    private const string RetryAction = "retry";
    private static readonly TimeSpan RetryActionLifetime = TimeSpan.FromMinutes(5);

    private readonly ISlackActionSigner _signing;
    private readonly IGrainFactory _grains;
    private readonly SlackConnectionAccessDecider _access;
    private readonly AgentSessionRetryService _retries;
    private readonly TimeProvider _time;

    public SlackRetryActionService(
        ISlackActionSigner signing,
        IGrainFactory grains,
        SlackConnectionAccessDecider access,
        AgentSessionRetryService retries,
        TimeProvider time)
    {
        _signing = signing;
        _grains = grains;
        _access = access;
        _retries = retries;
        _time = time;
    }

    public SlackRetryActionService(
        ISlackActionSigner signing,
        IGrainFactory grains,
        TimeProvider time)
        : this(signing, grains, null!, null!, time)
    {
    }

    public async Task<SlackRetryAction?> CreateRetryActionAsync(
        AgentConnection connection,
        string sessionId,
        string turnId,
        SlackMessageIdentity source,
        string? threadTs,
        CancellationToken ct = default)
    {
        var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turn = (await session.ListTurnsAsync())
            .SingleOrDefault(candidate => string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        if (turn is null
            || turn.Status != AgentTurnStatus.Failed
            || !AgentSessionRetryPolicy.IsRetryable(turn.Result?.FailureCategory))
            return null;

        var initial = await session.GetInitialLaunchAsync();
        var provenance = initial?.Input?.Provenance;
        var initiator = provenance?.MemberId;
        if (!IsBoundToConnection(provenance, connection.Id)
            || string.IsNullOrWhiteSpace(initiator))
            return null;

        var expiresAt = _time.GetUtcNow().Add(RetryActionLifetime);
        var payload = new SlackRetryActionPayload(
            Version: "v1",
            Action: RetryAction,
            ConnectionId: connection.Id,
            SessionId: sessionId,
            TurnId: turnId,
            ConversationId: source.ConversationId,
            MessageTs: source.MessageTs,
            ThreadTs: threadTs,
            ActorSlackUserId: initiator,
            InitiatorSlackUserId: initiator,
            Nonce: Guid.NewGuid().ToString("N"),
            ExpiresAt: expiresAt,
            Signature: null);
        var signature = await _signing.TrySignAsync(connection, Canonical(payload), ct);
        if (signature is null)
            return null;

        var value = JSON.Serialize(payload with { Signature = signature });
        return new SlackRetryAction(RetryActionId, value, expiresAt, BuildRetryBlocks(value));
    }

    public async Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        SlackLeaseContext leaseContext,
        CancellationToken ct = default)
    {
        if (!string.Equals(request.EventType, "block_actions", StringComparison.Ordinal)
            || !string.Equals(request.ActionId, RetryActionId, StringComparison.Ordinal))
            return Rejected("unsupported_action", "This action is not supported.");

        var payload = await VerifyAsync(connection, request.ActionValue, ct);
        if (payload is null)
            return Rejected("invalid_action", "This Retry action is invalid.");
        if (payload.ExpiresAt <= _time.GetUtcNow())
            return Rejected("expired", "This Retry action has expired.");
        if (!string.Equals(payload.ConnectionId, connection.Id, StringComparison.Ordinal)
            || !string.Equals(request.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(payload.ConversationId, request.ConversationId, StringComparison.Ordinal))
            return Rejected("stale_action", "This Retry action no longer matches the active Slack Connection.");
        if (!string.Equals(payload.ActorSlackUserId, request.ActorSlackUserId, StringComparison.Ordinal))
            return Rejected("unauthorized", "This Retry action belongs to a different Slack member.");

        var session = _grains.GetGrain<IAgentSessionGrain>(payload.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        var provenance = initial?.Input?.Provenance;
        var initiator = provenance?.MemberId;
        if (!IsBoundToConnection(provenance, connection.Id)
            || !string.Equals(initiator, payload.InitiatorSlackUserId, StringComparison.Ordinal)
            || !CanRetry(connection, initiator, request.ActorSlackUserId))
            return Rejected("unauthorized", "Only the Connection Owner or the session initiator may retry this Turn.");

        var access = await _access.EvaluateAsync(
            connection,
            request.ActorSlackUserId,
            request.TeamId,
            request.ConversationId,
            IsDirectMessage(request.ConversationId),
            leaseContext,
            ct);
        if (!access.Allowed)
            return Rejected("unauthorized", access.Reason);

        var retry = await _retries.RetryAsync(
            projectId,
            payload.SessionId,
            payload.TurnId,
            payload.Nonce,
            ct);
        return retry.Outcome switch
        {
            AgentSessionRetryOutcome.NoLongerRetryable =>
                Rejected("no_longer_retryable", "That Turn is no longer retryable."),
            AgentSessionRetryOutcome.AcceptedPending =>
                Confirmed("accepted_pending", retry.ResultText ?? "Retry attempt accepted and is pending dispatch."),
            AgentSessionRetryOutcome.Accepted or AgentSessionRetryOutcome.Finished =>
                Confirmed("attempt_accepted", retry.ResultText ?? "Retry attempt accepted."),
            _ => Rejected("no_longer_retryable", "That Turn is no longer retryable."),
        };
    }

    public async Task<SlackRetryActionPayload?> VerifyAsync(
        AgentConnection connection,
        string actionValue,
        CancellationToken ct = default)
    {
        SlackRetryActionPayload? payload;
        try
        {
            payload = JSON.Deserialize<SlackRetryActionPayload>(actionValue);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || !string.Equals(payload.Version, "v1", StringComparison.Ordinal)
            || !string.Equals(payload.Action, RetryAction, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.ConnectionId)
            || string.IsNullOrWhiteSpace(payload.SessionId)
            || string.IsNullOrWhiteSpace(payload.TurnId)
            || string.IsNullOrWhiteSpace(payload.ConversationId)
            || string.IsNullOrWhiteSpace(payload.MessageTs)
            || string.IsNullOrWhiteSpace(payload.ActorSlackUserId)
            || string.IsNullOrWhiteSpace(payload.InitiatorSlackUserId)
            || string.IsNullOrWhiteSpace(payload.Nonce)
            || string.IsNullOrWhiteSpace(payload.Signature))
            return null;

        return await _signing.VerifyAsync(
                connection,
                Canonical(payload with { Signature = null }),
                payload.Signature,
                ct)
            ? payload
            : null;
    }

    internal static string Canonical(SlackRetryActionPayload payload) => string.Join(
        "\n",
        payload.Version,
        payload.Action,
        payload.ConnectionId,
        payload.SessionId,
        payload.TurnId,
        payload.ConversationId,
        payload.MessageTs,
        payload.ThreadTs ?? string.Empty,
        payload.ActorSlackUserId,
        payload.InitiatorSlackUserId,
        payload.Nonce,
        payload.ExpiresAt.ToUnixTimeMilliseconds());

    private static bool IsBoundToConnection(AgentSessionInputProvenance? provenance, string connectionId) =>
        string.Equals(provenance?.ProviderKind, "slack", StringComparison.Ordinal)
        && string.Equals(provenance?.ConnectionId, connectionId, StringComparison.Ordinal);

    private static bool CanRetry(AgentConnection connection, string? initiator, string actorSlackUserId) =>
        string.Equals(connection.OwnerSlackUserId, actorSlackUserId, StringComparison.Ordinal)
        || string.Equals(initiator, actorSlackUserId, StringComparison.Ordinal);

    private static bool IsDirectMessage(string conversationId) =>
        conversationId.StartsWith("D", StringComparison.Ordinal);

    private static SlackTurnControlResult Rejected(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static SlackTurnControlResult Confirmed(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static JsonElement BuildPresentationBlocks(string text) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text },
            },
        });

    private static JsonElement BuildRetryBlocks(string value) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "actions",
                block_id = "mohist-turn-retry",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Retry" },
                        style = "primary",
                        action_id = RetryActionId,
                        value,
                    },
                },
            },
        });
}

public sealed record SlackRetryAction(
    string ActionId,
    string ActionValue,
    DateTimeOffset ExpiresAt,
    JsonElement Blocks);

public sealed record SlackRetryActionPayload(
    string Version,
    string Action,
    string ConnectionId,
    string SessionId,
    string TurnId,
    string ConversationId,
    string MessageTs,
    string? ThreadTs,
    string ActorSlackUserId,
    string InitiatorSlackUserId,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string? Signature);
