using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack;

public enum SlackThreadHistoryReadOutcome
{
    Empty,
    Imported,
    Refused,
}

public sealed record SlackThreadHistoryReadResult(
    SlackThreadHistoryReadOutcome Outcome,
    IReadOnlyList<SlackConversationMessage> Messages,
    string? FailureReason,
    int TotalFetched);

public sealed class SlackThreadHistoryReader : IScopedService
{
    /// <summary>
    /// Stable marker prepended to the oldest-dropped tail when the
    /// fetched thread history exceeds the configured character budget.
    /// The same string is also surfaced in the Slack acceptance reply so
    /// the two attestations cannot drift.
    /// </summary>
    public const string TruncationMarkerFormat = "{0} oldest messages omitted";

    private readonly ISlackApiClient _slack;
    private readonly ISecretStore _secrets;
    private readonly IOptions<SlackProviderOptions> _options;

    public SlackThreadHistoryReader(
        ISlackApiClient slack,
        ISecretStore secrets,
        IOptions<SlackProviderOptions> options)
    {
        _slack = slack;
        _secrets = secrets;
        _options = options;
    }

    public async Task<SlackThreadHistoryReadResult> ReadAsync(
        string projectId,
        string connectionId,
        string conversationId,
        string threadTs,
        string mentionTs,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadTs);
        ArgumentException.ThrowIfNullOrWhiteSpace(mentionTs);

        var token = await _secrets.LoadAsync(
            new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
        if (token is null || token.Length == 0)
            return Refused("missing bot token");
        var botToken = Encoding.UTF8.GetString(token);

        var depthCap = Math.Max(1, _options.Value.StartupContextPaginationDepthCap);
        if (!TryReadMessageTs(mentionTs, out var mentionBoundary))
            return Refused("mention timestamp is not readable");

        var collected = new List<SlackConversationMessage>();
        var reachedMention = false;
        var paginationComplete = false;
        string? cursor = null;
        for (var page = 0; page < depthCap; page++)
        {
            SlackConversationsRepliesPage response;
            try
            {
                response = await _slack.ConversationsRepliesAsync(
                    conversationId, threadTs, cursor, botToken, ct);
            }
            catch (HttpRequestException)
            {
                return Refused("transport failure");
            }
            catch (TaskCanceledException)
            {
                return Refused("transport failure");
            }

            if (!response.Ok)
                return Refused($"slack rejected the read: {response.Error ?? "unknown_error"}");
            foreach (var message in response.Messages ?? [])
            {
                if (message is null)
                    continue;
                if (!TryReadMessageTs(message.Ts, out var messageTs))
                    continue;
                if (messageTs >= mentionBoundary)
                {
                    reachedMention = true;
                    break;
                }
                collected.Add(message);
            }

            if (reachedMention)
                break;

            var nextCursor = response.ResponseMetadata?.NextCursor;
            if (string.IsNullOrWhiteSpace(nextCursor))
            {
                paginationComplete = true;
                break;
            }
            cursor = nextCursor;
        }

        if (!reachedMention && !paginationComplete)
            return Refused("pagination depth cap reached before the mention");

        if (collected.Count == 0)
            return Empty(0);
        return new SlackThreadHistoryReadResult(
            SlackThreadHistoryReadOutcome.Imported,
            collected,
            null,
            collected.Count);
    }

    public static (string Text, string? TruncationMarker, int OmittedCount) ApplyBudget(
        IReadOnlyList<SlackConversationMessage> messages,
        int characterBudget)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (characterBudget <= 0)
            return (string.Empty, null, 0);

        var ordered = messages
            .Where(m => m is not null && TryReadMessageTs(m.Ts, out _))
            .OrderBy(m =>
            {
                TryReadMessageTs(m.Ts, out var ts);
                return ts;
            })
            .ToList();
        if (ordered.Count == 0)
            return (string.Empty, null, 0);

        var total = RenderTranscript(ordered);
        if (total.Length <= characterBudget)
            return (total, null, 0);

        var kept = new List<SlackConversationMessage>(ordered.Count);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var candidate = new List<SlackConversationMessage>(kept.Count + 1) { ordered[i] };
            candidate.AddRange(kept);
            var rendered = RenderTranscript(candidate);
            if (rendered.Length > characterBudget)
                break;
            kept = candidate;
        }

        if (kept.Count == ordered.Count)
            return (total, null, 0);

        var omitted = ordered.Count - kept.Count;
        var marker = string.Format(
            CultureInfo.InvariantCulture,
            TruncationMarkerFormat,
            omitted);
        return (RenderTranscript(kept), marker, omitted);
    }

    private static string RenderTranscript(IReadOnlyList<SlackConversationMessage> messages)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');
            var message = messages[i];
            var author = message.User ?? message.BotId ?? "unknown";
            var text = message.Text ?? string.Empty;
            builder.Append(author);
            builder.Append(": ");
            builder.Append(text);
        }
        return builder.ToString();
    }

    private static SlackThreadHistoryReadResult Empty(int totalFetched) =>
        new(SlackThreadHistoryReadOutcome.Empty, Array.Empty<SlackConversationMessage>(), null, totalFetched);

    private static SlackThreadHistoryReadResult Refused(string reason) =>
        new(SlackThreadHistoryReadOutcome.Refused, Array.Empty<SlackConversationMessage>(), reason, 0);

    private static bool TryReadMessageTs(string? ts, out double value)
    {
        if (string.IsNullOrWhiteSpace(ts))
        {
            value = 0;
            return false;
        }
        return double.TryParse(ts, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
