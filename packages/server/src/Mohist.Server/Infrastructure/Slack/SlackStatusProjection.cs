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
                FallbackText: "Received. I am checking the task and will post the result here.",
                FallbackDispatchRef: DispatchRef(source, "status"),
                StatusDispatchRef: DispatchRef(source, "status"))),
            threadTs ?? source.MessageTs), ct);

    public async Task<SlackOutboxEnqueueResult> EnqueueWorkingAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string? progressDispatchRef = null,
        JsonElement? blocks = null,
        CancellationToken ct = default)
    {
        var result = await _outbox.UpsertReplaceableProgressAsync(new SlackOutboxDraft(
            projectId,
            connectionId,
            source.WorkspaceTeamId,
            source.ConversationId,
            SlackOutboxKinds.ReplaceableProgress,
            progressDispatchRef ?? DispatchRef(source, "progress"),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                "Working...",
                ClientMessageId: DispatchRef(source, "status"),
                FallbackDispatchRef: DispatchRef(source, "status"),
                StatusDispatchRef: DispatchRef(source, "status"),
                Blocks: blocks)),
            threadTs ?? source.MessageTs), ct);

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
        await EnqueueReactionAsync(
            projectId,
            connectionId,
            source,
            threadTs,
            "working-add",
            SlackDeliveryOperations.ReactionAdd,
            WorkingReaction,
            "Working...",
            ct);
        return result;
    }

    public async Task<SlackOutboxEnqueueResult> EnqueueTerminalAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string status,
        string text,
        string? terminalDispatchRef = null,
        string? progressDispatchRef = null,
        CancellationToken ct = default)
    {
        var dispatchRef = progressDispatchRef ?? DispatchRef(source, "progress");
        var entries = await _outbox.ListAsync(projectId, connectionId, ct);
        var progress = entries.Entries.FirstOrDefault(entry =>
            entry.Kind == SlackOutboxKinds.ReplaceableProgress && entry.DispatchRef == dispatchRef);
        var projectionSource = source;
        var progressPayload = progress is null ? null : TryReadPayload(progress.PayloadJson);
        if (progressPayload?.StatusDispatchRef is { } statusDispatchRef
            && TryReadSource(statusDispatchRef, out var parsedSource))
        {
            projectionSource = parsedSource;
        }
        var received = entries.Entries.FirstOrDefault(entry =>
            entry.Kind == SlackOutboxKinds.ReactionMutation
            && entry.DispatchRef == DispatchRef(projectionSource, "received"));
        var providerIdentity = progressPayload?.ProviderMessageIdentity;
        providerIdentity ??= received is null ? null : ReadProviderIdentity(received.PayloadJson);
        var hadProgress = progress is not null;
        var operation = providerIdentity is null ? SlackDeliveryOperations.PostMessage : SlackDeliveryOperations.ChatUpdate;
        var payload = new SlackDeliveryPayload(
            operation,
            text,
            ClientMessageId: terminalDispatchRef ?? DispatchRef(source, "terminal"),
            ProviderMessageIdentity: providerIdentity,
            FallbackText: text,
            FallbackDispatchRef: $"{terminalDispatchRef ?? DispatchRef(source, "terminal")}:fallback",
            StatusDispatchRef: DispatchRef(projectionSource, "status"));
        var kind = string.Equals(status, "completed", StringComparison.Ordinal)
            ? SlackOutboxKinds.TerminalResult
            : SlackOutboxKinds.ExplicitFailure;
        var draft = new SlackOutboxDraft(
            projectId,
            connectionId,
            source.WorkspaceTeamId,
            source.ConversationId,
            kind,
            terminalDispatchRef ?? DispatchRef(source, "terminal"),
            JsonSerializer.Serialize(payload),
            threadTs);
        var promoted = await _outbox.PromotePendingProgressAsync(draft, dispatchRef, ct);
        var result = promoted ?? await _outbox.EnqueueRequiredAsync(draft, ct);
        if (hadProgress)
        {
            await EnqueueReactionAsync(
                projectId,
                connectionId,
                projectionSource,
                threadTs,
                "terminal-remove-working",
                SlackDeliveryOperations.ReactionRemove,
                WorkingReaction,
                null,
                ct);
            await EnqueueReactionAsync(
                projectId,
                connectionId,
                projectionSource,
                threadTs,
                "terminal-add",
                SlackDeliveryOperations.ReactionAdd,
                ReactionFor(status),
                null,
                ct);
        }
        return result;
    }

    public static string DispatchRef(SlackMessageIdentity source, string phase) =>
        $"slack-status:{source.AsKey()}:{phase}";

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

    private Task<SlackOutboxEnqueueResult> EnqueueReactionAsync(
        string projectId,
        string connectionId,
        SlackMessageIdentity source,
        string? threadTs,
        string phase,
        string operation,
        string reaction,
        string? fallbackText,
        CancellationToken ct) =>
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
                StatusDispatchRef: DispatchRef(source, "status"))),
            threadTs ?? source.MessageTs), ct);
}
