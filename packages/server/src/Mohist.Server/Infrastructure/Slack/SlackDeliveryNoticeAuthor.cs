using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Authors the bounded system delivery notice for a content delivery that
/// settled without a confirmed outcome.
/// <para>
/// The notice is one outbox intent per original delivery, keyed by
/// <c>slack-delivery-notice:{originalDeliveryId}</c>, so a replayed ack, a
/// restart, an operator retry, or a sweep rerun converges on the same row
/// instead of posting again. It reuses the terminal explicit-failure kind so
/// the outbox keeps one state machine and one capacity / dead-letter policy,
/// and it is posted in the original thread so it cannot redirect or duplicate
/// the Agent reply.
/// </para>
/// <para>
/// Only content kinds (terminal Agent reply, replaceable Session card) get a
/// notice, and only for the settlements <see cref="SlackOutboxStates.DeliveryUncertain"/>
/// and <see cref="SlackOutboxStates.DeadLettered"/>. Reactions, explicit
/// failures, and notices themselves are excluded, so no notice about a notice
/// can exist.
/// </para>
/// </summary>
public sealed class SlackDeliveryNoticeAuthor : IScopedService
{
    /// <summary>
    /// Reserved dispatch-key namespace. A notice is never deduplicated against
    /// the Agent-crash system failure, which keys on the AgentJob dispatch
    /// reference instead.
    /// </summary>
    public const string DispatchPrefix = "slack-delivery-notice:";

    /// <summary>Payload fact: the outcome is still unknown.</summary>
    public const string Uncertain = "delivery_uncertain";

    /// <summary>Payload fact: retries stopped without a confirmed outcome.</summary>
    public const string Exhausted = "delivery_exhausted";

    private readonly SlackOutboxStore _outbox;
    private readonly SlackSessionCardBlocksBuilder _blocks;
    private readonly ILogger<SlackDeliveryNoticeAuthor> _log;

    public SlackDeliveryNoticeAuthor(
        SlackOutboxStore outbox,
        SlackSessionCardBlocksBuilder blocks,
        ILogger<SlackDeliveryNoticeAuthor> log)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public enum Subject
    {
        AgentReply,
        SessionCard,
    }

    /// <summary>
    /// Posts at most one notice for the original delivery. Returns false when
    /// the delivery needs none, when the notice already exists, when the owner
    /// is no longer live, or when authoring failed — a notice is a visibility
    /// aid and never fails the settlement that produced it.
    /// </summary>
    public async Task<bool> TryNoticeAsync(
        string projectId,
        string ownerKind,
        string connectionId,
        string deliveryId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        try
        {
            var row = await _outbox
                .FindRowAsync(projectId, ownerKind, connectionId, deliveryId, ct)
                .ConfigureAwait(false);
            if (row is null || !IsContentKind(row.Kind))
                return false;

            var notice = row.State switch
            {
                SlackOutboxStates.DeliveryUncertain => Uncertain,
                SlackOutboxStates.DeadLettered => Exhausted,
                _ => null,
            };
            if (notice is null)
                return false;

            var original = TryParsePayload(row.PayloadJson);
            if (original is null)
                return false;

            // Once an intent was unknown the uncertainty timestamp survives
            // every later settlement, so exhaustion cannot be described as a
            // delivery that never happened.
            var possiblyDelivered = row.State == SlackOutboxStates.DeliveryUncertain
                || row.DeliveryUncertainAt is not null;
            var subject = row.Kind == SlackOutboxKinds.ReplaceableProgress
                ? Subject.SessionCard
                : Subject.AgentReply;
            var text = Render(subject, notice, possiblyDelivered, original.SessionId);
            var sessionId = string.IsNullOrWhiteSpace(original.SessionId)
                ? null
                : original.SessionId;
            JsonElement? blocks = sessionId is null
                ? null
                : await _blocks.BuildAsync(projectId, sessionId, controlBlocks: null).ConfigureAwait(false);

            var dispatchRef = $"{DispatchPrefix}{row.Id}";
            var result = await _outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
                row.ProjectId,
                row.ConnectionId,
                row.WorkspaceTeamId,
                row.ConversationId,
                SlackOutboxKinds.ExplicitFailure,
                dispatchRef,
                JsonSerializer.Serialize(new SlackDeliveryPayload(
                    SlackDeliveryOperations.PostMessage,
                    text,
                    ClientMessageId: dispatchRef,
                    FallbackText: text,
                    FallbackDispatchRef: $"{dispatchRef}:fallback",
                    Blocks: blocks,
                    SessionId: sessionId,
                    Notice: notice,
                    PossiblyDelivered: possiblyDelivered)),
                row.ThreadTs,
                row.OwnerKind), ct).ConfigureAwait(false);

            if (result.Suppressed)
            {
                _log.LogInformation(
                    "Slack delivery notice for row {DeliveryId} (ConnectionId={ConnectionId}) was suppressed: the owner is no longer live",
                    deliveryId,
                    connectionId);
                return false;
            }

            _log.LogInformation(
                "Slack delivery notice {NoticeId} ({Notice}) authored for row {DeliveryId} (ConnectionId={ConnectionId}, Kind={Kind}, PossiblyDelivered={PossiblyDelivered})",
                result.Id,
                notice,
                deliveryId,
                connectionId,
                row.Kind,
                possiblyDelivered);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Slack delivery notice for row {DeliveryId} (ConnectionId={ConnectionId}) could not be authored",
                deliveryId,
                connectionId);
            return false;
        }
    }

    internal static bool IsContentKind(string kind) =>
        kind is SlackOutboxKinds.TerminalResult or SlackOutboxKinds.ReplaceableProgress;

    /// <summary>
    /// The notice states the delivery fact only: which projection it concerns,
    /// whether the content may already be visible, and that nothing re-runs
    /// automatically. It never contains Agent text and never claims an
    /// AgentTurn result.
    /// </summary>
    public static string Render(Subject subject, string notice, bool possiblyDelivered, string? sessionId)
    {
        var what = subject == Subject.SessionCard ? "The Session card" : "The Agent reply";
        var headline = notice == Uncertain
            ? $"{what} may already be visible in this thread; Mohist could not confirm that it reached Slack."
            : possiblyDelivered
                ? $"{what} may already be visible; Mohist could not confirm it and stopped retrying."
                : $"{what} was generated but not delivered; Mohist stopped retrying.";
        var reference = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : $" Session: {sessionId}.";
        return $"Delivery notice: {headline} Check the thread and the Session before sending it again; nothing is re-run automatically.{reference}";
    }

    private static SlackDeliveryPayload? TryParsePayload(string payloadJson)
    {
        try
        {
            return SlackDeliveryPayload.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
