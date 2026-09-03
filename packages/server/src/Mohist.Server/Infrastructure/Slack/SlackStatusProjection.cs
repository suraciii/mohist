using System.Text.Json;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackStatusProjection : IScopedService
{
    private const string ReceivedReaction = "eyes";
    private const string WorkingReaction = "hourglass_flowing_sand";
    private const string CompletedReaction = "white_check_mark";
    private const string AttentionReaction = "warning";

    private readonly SlackOutboxStore _outbox;

    public SlackStatusProjection(SlackOutboxStore outbox) => _outbox = outbox;

    public Task<SlackOutboxEnqueueResult> EnqueueReceivedAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        CancellationToken ct = default) =>
        _outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            projectId,
            connectionId,
            source.WorkspaceTeamId,
            source.ConversationId,
            SlackOutboxKinds.ReactionMutation,
            DispatchRef(source, "received"),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.ReactionAdd,
                TargetMessageIdentity: new SlackProviderMessageIdentity(source.ConversationId, source.MessageTs),
                Reaction: ReceivedReaction,
                FallbackText: string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                    ? null
                    : "Received. I am checking the task and will post the result here.",
                FallbackDispatchRef: string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                    ? null
                    : DispatchRef(source, "status"),
                StatusDispatchRef: DispatchRef(source, "status"))),
            threadTs ?? source.MessageTs,
            OwnerKindFor(projectId)), ct);

    public async Task<SlackOutboxEnqueueResult> EnqueueWorkingAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string? progressDispatchRef = null,
        JsonElement? blocks = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (!string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal))
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // Queue the receipt removal first. The adapter claims rows in their
        // durable insertion order, so a working message cannot become
        // visible before the receipt transition has been requested.
        await EnqueueReactionAsync(
            projectId,
            connectionId,
            source,
            threadTs,
            "working-remove-received",
            SlackDeliveryOperations.ReactionRemove,
            ReceivedReaction,
            null,
            ct);

        // Manager liveness is deliberately reaction-only. The Agent's reply
        // action is the sole conversational delivery path; do not create a
        // Server-authored progress message for Manager turns.
        if (string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal))
        {
            return await EnqueueReactionAsync(
                projectId,
                connectionId,
                source,
                threadTs,
                "working-add",
                SlackDeliveryOperations.ReactionAdd,
                WorkingReaction,
                null,
                ct);
        }

        var sessionCardText = BuildSessionCardText(sessionId!);
        var result = await _outbox.UpsertReplaceableProgressAsync(new SlackOutboxDraft(
            projectId,
            connectionId,
            source.WorkspaceTeamId,
            source.ConversationId,
            SlackOutboxKinds.ReplaceableProgress,
            progressDispatchRef ?? DispatchRef(source, "progress"),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                sessionCardText,
                ClientMessageId: DispatchRef(source, "status"),
                FallbackDispatchRef: DispatchRef(source, "status"),
                StatusDispatchRef: DispatchRef(source, "status"),
                Blocks: blocks)),
            threadTs ?? source.MessageTs,
            OwnerKindFor(projectId)), ct);

        await EnqueueReactionAsync(
            projectId,
            connectionId,
            source,
            threadTs,
            "working-add",
            SlackDeliveryOperations.ReactionAdd,
            WorkingReaction,
            sessionCardText,
            ct);
        return result;
    }

    public async Task<SlackOutboxEnqueueResult> EnqueueFailureAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string text,
        string? failureDispatchRef = null,
        string? progressDispatchRef = null,
        JsonElement? blocks = null,
        CancellationToken ct = default)
    {
        var dispatchRef = failureDispatchRef ?? DispatchRef(source, "system-failure");
        var payload = new SlackDeliveryPayload(
            SlackDeliveryOperations.PostMessage,
            text,
            ClientMessageId: dispatchRef,
            FallbackText: text,
            FallbackDispatchRef: $"{dispatchRef}:fallback",
            Blocks: blocks);
        var draft = new SlackOutboxDraft(
            projectId,
            connectionId,
            source.WorkspaceTeamId,
            source.ConversationId,
            SlackOutboxKinds.ExplicitFailure,
            dispatchRef,
            JsonSerializer.Serialize(payload),
            threadTs,
            OwnerKindFor(projectId));
        var result = await _outbox.EnqueueRequiredAsync(draft, ct);
        await FinalizeLivenessAsync(
            projectId,
            connectionId,
            source,
            threadTs,
            "failed",
            progressDispatchRef,
            ct);
        return result;
    }

    /// <summary>
    /// Finalizes liveness for a completed turn WITHOUT authoring a reply:
    /// transitions the Working reaction to the terminal reaction
    /// (Completed/Failed). The reply body itself comes from the Agent's
    /// reply action (<c>mo slack message send</c>); silence is a
    /// legitimate outcome, so this never injects text. Safe to call
    /// after a reply action already created an independent terminal row — it
    /// looks for either a replaceable progress row or the terminal row for
    /// the same dispatch reference.
    /// </summary>
    public async Task FinalizeLivenessAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string status,
        string? progressDispatchRef = null,
        CancellationToken ct = default)
    {
        var dispatchRef = progressDispatchRef ?? DispatchRef(source, "progress");
        var entries = await _outbox.ListAsync(
            projectId,
            connectionId,
            ct,
            OwnerKindFor(projectId));
        var projectionSource = source;
        var hadWorking = false;
        foreach (var entry in entries.Entries)
        {
            var payload = TryReadPayload(entry.PayloadJson);
            var isWorkingReaction = entry.Kind == SlackOutboxKinds.ReactionMutation
                && entry.DispatchRef == DispatchRef(source, "working-add")
                && payload?.Operation == SlackDeliveryOperations.ReactionAdd
                && payload.Reaction == WorkingReaction;
            var isProgressRow = (entry.DispatchRef == dispatchRef
                    || payload?.ProgressDispatchRef == dispatchRef)
                && (entry.Kind == SlackOutboxKinds.ReplaceableProgress
                    || entry.Kind == SlackOutboxKinds.TerminalResult
                    || entry.Kind == SlackOutboxKinds.ExplicitFailure);
            if (!isWorkingReaction && !isProgressRow)
                continue;

            hadWorking = true;
            if (payload?.StatusDispatchRef is { } statusDispatchRef
                && TryReadSource(statusDispatchRef, out var parsedSource))
            {
                projectionSource = parsedSource;
            }
            break;
        }
        // Fast completion can race the working projection. Terminal
        // convergence must still close receipt state and add one terminal
        // reaction even when no progress row was stored.
        if (hadWorking)
        {
            await EnqueueReactionAsync(
                projectId, connectionId, projectionSource, threadTs,
                "terminal-remove-working",
                SlackDeliveryOperations.ReactionRemove,
                WorkingReaction,
                null,
                ct);
        }
        await EnqueueReactionAsync(
            projectId, connectionId, projectionSource, threadTs,
            "terminal-remove-received",
            SlackDeliveryOperations.ReactionRemove,
            ReceivedReaction,
            null,
            ct);
        await EnqueueReactionAsync(
            projectId, connectionId, projectionSource, threadTs,
            "terminal-add",
            SlackDeliveryOperations.ReactionAdd,
            ReactionFor(status),
            null,
            ct,
            terminalStatus: status);
    }

    public static string DispatchRef(SlackMessageIdentity source, string phase) =>
        $"slack-status:{source.AsKey()}:{phase}";

    private static string BuildSessionCardText(string sessionId) =>
        SlackFinalReplyRenderer.AppendStableReference("Agent session.", sessionId, sessionId);

    public static string ReactionFor(string status) => status switch
    {
        "completed" => CompletedReaction,
        "failed" or "unknown" or "cancelled" or "needs_attention" => AttentionReaction,
        "working" => WorkingReaction,
        _ => ReceivedReaction,
    };

    private static SlackProviderMessageIdentity? ReadProviderIdentity(string payloadJson)
    {
        try
        {
            return SlackDeliveryPayload.Parse(payloadJson).ProviderMessageIdentity;
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

    private static SlackDeliveryPayload? TryReadPayload(string payloadJson)
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

    private static bool TryReadSource(string dispatchRef, out SlackMessageIdentity source)
    {
        const string prefix = "slack-status:";
        const string suffix = ":status";
        source = default;
        if (!dispatchRef.StartsWith(prefix, StringComparison.Ordinal)
            || !dispatchRef.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var key = dispatchRef[prefix.Length..^suffix.Length];
        var parts = key.Split('/');
        if (parts.Length != 3)
            return false;

        source = new SlackMessageIdentity(parts[0], parts[1], parts[2]);
        return source.Validate().Length == 0;
    }

    private static string OwnerKindFor(string projectId) =>
        string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
            ? SlackDeliveryOwnerKinds.Manager
            : SlackDeliveryOwnerKinds.Connection;

    private Task<SlackOutboxEnqueueResult> EnqueueReactionAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string phase,
        string operation,
        string reaction,
        string? fallbackText,
        CancellationToken ct,
        string? terminalStatus = null) =>
        _outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            projectId,
            connectionId,
            source.WorkspaceTeamId,
            source.ConversationId,
            SlackOutboxKinds.ReactionMutation,
            DispatchRef(source, phase),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                operation,
                TargetMessageIdentity: new SlackProviderMessageIdentity(source.ConversationId, source.MessageTs),
                Reaction: reaction,
                FallbackText: fallbackText,
                FallbackDispatchRef: DispatchRef(source, "status"),
                StatusDispatchRef: DispatchRef(source, "status"),
                TerminalStatus: terminalStatus)),
            threadTs ?? source.MessageTs,
            OwnerKindFor(projectId)), ct);
}
