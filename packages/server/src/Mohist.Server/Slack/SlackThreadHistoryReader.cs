using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
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

    public Task<SlackThreadHistoryReadResult> ReadAsync(
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

        return Task.FromResult(Empty(0));
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
