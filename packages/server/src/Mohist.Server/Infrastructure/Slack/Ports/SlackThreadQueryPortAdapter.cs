using System.Globalization;
using System.Text.Json;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Production <see cref="ISlackThreadQueryPort"/>: one
/// <c>conversations.replies</c> page with the Connection's Bot token through
/// <see cref="SlackApiTransport"/>. The adapter owns the wire shape: it maps
/// the provider payload onto normalized messages, keeps message timestamps as
/// strings, keeps source order, and fails closed on unreadable pagination
/// instead of claiming a completed thread.
/// </summary>
public sealed class SlackThreadQueryPortAdapter(SlackApiTransport transport) : ISlackThreadQueryPort
{
    public const string ConversationsRepliesEndpoint = "conversations.replies";

    public async Task<SlackThreadPage> ReadPageAsync(
        SlackThreadPageQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ThreadRootTs);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel"] = query.ConversationId,
            ["ts"] = query.ThreadRootTs,
            ["limit"] = query.Limit.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(query.Continuation))
            form["cursor"] = query.Continuation;

        var response = await transport.PostFormAsync(
            ConversationsRepliesEndpoint,
            form,
            query.BotToken,
            ct).ConfigureAwait(false);

        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => ParsePage(response.Body, query.ThreadRootTs),
            SlackApiCallOutcome.RateLimited => SlackThreadPage.RateLimited(response.RetryAfter),
            SlackApiCallOutcome.Rejected => SlackThreadPage.ProviderRejected(
                response.Error ?? "conversations_replies_rejected"),
            SlackApiCallOutcome.Unparseable => SlackThreadPage.InvalidResponse("unparseable_response"),
            _ => SlackThreadPage.TransportError,
        };
    }

    private static SlackThreadPage ParsePage(JsonDocument? body, string threadRootTs)
    {
        if (body is null)
            return SlackThreadPage.InvalidResponse("unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("has_more", out var hasMore)
                || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return SlackThreadPage.InvalidResponse();
            }

            var more = hasMore.GetBoolean();
            var cursor = ReadNextCursor(root);
            // A completion claim and a continuation are mutually exclusive:
            // has_more without a cursor truncates the traversal silently, and a
            // cursor without has_more contradicts the provider's own answer.
            if (more && cursor is null)
                return SlackThreadPage.InvalidResponse();
            if (!more && cursor is not null)
                return SlackThreadPage.InvalidResponse();

            var normalized = new List<SlackThreadMessage>(messages.GetArrayLength());
            foreach (var message in messages.EnumerateArray())
            {
                if (!TryReadMessage(message, threadRootTs, out var normalizedMessage))
                    return SlackThreadPage.InvalidResponse("invalid_message_response");
                normalized.Add(normalizedMessage);
            }

            return SlackThreadPage.Ok(normalized, more ? cursor : null);
        }
    }

    private static string? ReadNextCursor(JsonElement root)
    {
        if (!root.TryGetProperty("response_metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("next_cursor", out var cursor)
            || cursor.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = cursor.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryReadMessage(
        JsonElement message,
        string threadRootTs,
        out SlackThreadMessage normalized)
    {
        normalized = null!;
        if (message.ValueKind != JsonValueKind.Object)
            return false;

        var ts = ReadString(message, "ts");
        if (string.IsNullOrWhiteSpace(ts))
            return false;

        var subtype = ReadString(message, "subtype");
        var editedTs = message.TryGetProperty("edited", out var edited) && edited.ValueKind == JsonValueKind.Object
            ? ReadString(edited, "ts")
            : null;
        normalized = new SlackThreadMessage(
            Ts: ts,
            AuthorUserId: ReadString(message, "user"),
            AuthorBotId: ReadString(message, "bot_id"),
            Text: ReadString(message, "text") ?? string.Empty,
            ThreadRoot: string.Equals(ts, threadRootTs, StringComparison.Ordinal),
            Edited: editedTs is not null,
            EditedTs: editedTs,
            Deleted: subtype is "message_deleted" or "tombstone",
            UnavailableContent: ReadUnavailableContent(message),
            Permalink: ReadString(message, "permalink"));
        return true;
    }

    /// <summary>
    /// Names the content this read did not retrieve. Blocks are only named when
    /// they are the message's whole rendered body: a block message that also
    /// carries text is already represented by that text.
    /// </summary>
    private static IReadOnlyList<string> ReadUnavailableContent(JsonElement message)
    {
        var kinds = new List<string>(3);
        if (HasNonEmptyArray(message, "files"))
            kinds.Add("file");
        if (HasNonEmptyArray(message, "attachments"))
            kinds.Add("attachment");
        if (HasNonEmptyArray(message, "blocks") && string.IsNullOrWhiteSpace(ReadString(message, "text")))
            kinds.Add("rich_text");
        return kinds;
    }

    private static bool HasNonEmptyArray(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var candidate)
        && candidate.ValueKind == JsonValueKind.Array
        && candidate.GetArrayLength() > 0;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var candidate)
        && candidate.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(candidate.GetString())
            ? candidate.GetString()
            : null;
}
