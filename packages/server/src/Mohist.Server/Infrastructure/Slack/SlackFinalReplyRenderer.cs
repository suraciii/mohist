using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Server.Infrastructure.Slack;

public enum SlackFinalReplyStatus
{
    Completed,
    PartiallyCompleted,
    Cancelled,
    Blocked,
    Failed,
}

public sealed record SlackConfirmedAgentResult(
    string WorkLabel,
    SlackFinalReplyStatus Status,
    string? Summary = null,
    IReadOnlyList<string>? CompletedParts = null,
    IReadOnlyList<string>? KeyResults = null,
    string? BlockingReason = null,
    string? FailureReason = null,
    IReadOnlyList<string>? Actions = null,
    string? NextStep = null);

public sealed record SlackFinalReplyProjection(IReadOnlyList<string> Segments);

public static class SlackFinalReplyRenderer
{
    public const int DefaultMaximumSegmentLength = 3_000;

    private const int MaximumKeyResults = 3;
    private static readonly Regex SecretAssignment = new(
        "(?i)(?:\\\"(?:token|secret|api[_-]?key|password)[^\\\"]*\\\"\\s*:\\s*\\\"|(?:token|secret|api[_-]?key|password)\\s*[:=]\\s*)(?:[^\\\"\\s,}]+|[^\\\"]*\\\")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlackToken = new(
        "xox[baprs]-[A-Za-z0-9._-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SlackFinalReplyProjection Project(
        SlackConfirmedAgentResult result,
        int maximumSegmentLength = DefaultMaximumSegmentLength)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (maximumSegmentLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumSegmentLength));

        var workLabel = CleanRequiredText(result.WorkLabel, nameof(result.WorkLabel));
        var lines = new List<string>
        {
            BuildConclusion(result.Status, workLabel, result.Summary),
        };

        var reason = result.Status switch
        {
            SlackFinalReplyStatus.Blocked => CleanText(result.BlockingReason),
            SlackFinalReplyStatus.Failed => CleanText(result.FailureReason),
            _ => null,
        };
        if (reason is not null)
            lines.Add(result.Status == SlackFinalReplyStatus.Blocked
                ? $"Blocked because: {reason}"
                : $"Failure reason: {reason}");

        var facts = BuildKeyResults(result);
        if (facts.Count > 0)
        {
            lines.Add("Key results:");
            lines.AddRange(facts.Select(fact => $"- {fact}"));
        }

        var actions = CleanList(result.Actions);
        if (actions.Count > 0)
        {
            lines.Add("Actions:");
            lines.AddRange(actions.Select(action => $"- {action}"));
        }

        lines.Add($"Next step: {CleanText(result.NextStep) ?? DefaultNextStep(result.Status)}");

        return new SlackFinalReplyProjection(Segment(lines, maximumSegmentLength));
    }

    private static string BuildConclusion(
        SlackFinalReplyStatus status,
        string workLabel,
        string? summary)
    {
        var conclusion = status switch
        {
            SlackFinalReplyStatus.Completed => "Completed",
            SlackFinalReplyStatus.PartiallyCompleted => "Partially completed",
            SlackFinalReplyStatus.Cancelled => "Cancelled",
            SlackFinalReplyStatus.Blocked => "Blocked",
            SlackFinalReplyStatus.Failed => "Failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        var line = $"Conclusion: {conclusion} - {workLabel}.";
        var cleanSummary = CleanText(summary);
        return cleanSummary is null ? line : $"{line} {cleanSummary}";
    }

    private static List<string> BuildKeyResults(SlackConfirmedAgentResult result)
    {
        var facts = new List<string>(MaximumKeyResults);
        AddFacts(facts, result.CompletedParts, "Completed: ");
        AddFacts(facts, result.KeyResults, prefix: null);
        return facts;
    }

    private static void AddFacts(
        List<string> facts,
        IReadOnlyList<string>? values,
        string? prefix)
    {
        if (values is null)
            return;

        foreach (var value in values)
        {
            if (facts.Count == MaximumKeyResults)
                return;

            var cleanValue = CleanText(value);
            if (cleanValue is null)
                continue;

            var fact = prefix is null ? cleanValue : prefix + cleanValue;
            if (!facts.Contains(fact, StringComparer.Ordinal))
                facts.Add(fact);
        }
    }

    private static List<string> CleanList(IReadOnlyList<string>? values)
    {
        var cleanValues = new List<string>();
        if (values is null)
            return cleanValues;

        foreach (var value in values)
        {
            var cleanValue = CleanText(value);
            if (cleanValue is not null && !cleanValues.Contains(cleanValue, StringComparer.Ordinal))
                cleanValues.Add(cleanValue);
        }

        return cleanValues;
    }

    private static string CleanRequiredText(string? value, string parameterName) =>
        CleanText(value) ?? throw new ArgumentException("The work label is required.", parameterName);

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var clean = value.ReplaceLineEndings("\n").Trim();
        if (IsJsonObjectOrArray(clean))
            return null;

        clean = SecretAssignment.Replace(clean, "[REDACTED]");
        return SlackToken.Replace(clean, "[REDACTED]");
    }

    private static bool IsJsonObjectOrArray(string value)
    {
        if ((value[0] != '{' && value[0] != '[')
            || (value[^1] != '}' && value[^1] != ']'))
            return false;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DefaultNextStep(SlackFinalReplyStatus status) => status switch
    {
        SlackFinalReplyStatus.Completed => "Review the results and send the next request.",
        SlackFinalReplyStatus.PartiallyCompleted => "Review the completed parts and decide whether to continue.",
        SlackFinalReplyStatus.Cancelled => "Send a new request when you are ready to continue.",
        SlackFinalReplyStatus.Blocked => "Resolve the blocker, then ask me to continue.",
        SlackFinalReplyStatus.Failed => "Fix the reported problem, then retry.",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static IReadOnlyList<string> Segment(IReadOnlyList<string> lines, int maximumLength)
    {
        var segments = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            foreach (var part in SplitLine(line, maximumLength))
            {
                if (current.Length == 0)
                {
                    current.Append(part);
                    continue;
                }

                if (current.Length + 1 + part.Length <= maximumLength)
                {
                    current.Append('\n').Append(part);
                    continue;
                }

                segments.Add(current.ToString());
                current.Clear();
                current.Append(part);
            }
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments;
    }

    private static IEnumerable<string> SplitLine(string line, int maximumLength)
    {
        var remaining = line;
        while (remaining.Length > maximumLength)
        {
            var splitAt = remaining.LastIndexOf(' ', maximumLength - 1);
            if (splitAt < 1)
                splitAt = maximumLength;

            yield return remaining[..splitAt].TrimEnd();
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
            yield return remaining;
    }
}
