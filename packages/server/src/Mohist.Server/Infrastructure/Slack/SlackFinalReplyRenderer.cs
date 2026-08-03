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

public enum SlackFinalReplyDetailLevel
{
    Summary,
    Diagnostic,
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
    string? NextStep = null,
    IReadOnlyList<SlackConfirmedMachineResult>? MachineResults = null);

public sealed record SlackConfirmedMachineResult(string Label, string? Payload);

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
    private static readonly Regex SlackControlSyntax = new(
        @"<(?:(?:!|@|#)[^>\r\n]*|(?:https?|mailto|tel):[^>\r\n]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SlackFinalReplyProjection Project(
        SlackConfirmedAgentResult result,
        int maximumSegmentLength = DefaultMaximumSegmentLength,
        SlackFinalReplyDetailLevel detailLevel = SlackFinalReplyDetailLevel.Summary)
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

        var facts = BuildKeyResults(result, detailLevel);
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

        var safeLines = lines.Select(NeutralizeSlackControlSyntax).ToArray();
        return new SlackFinalReplyProjection(Segment(safeLines, maximumSegmentLength));
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

    private static List<string> BuildKeyResults(
        SlackConfirmedAgentResult result,
        SlackFinalReplyDetailLevel detailLevel)
    {
        var facts = new List<string>(MaximumKeyResults);
        AddFacts(facts, result.CompletedParts, "Completed: ");
        AddFacts(facts, result.KeyResults, prefix: null);
        AddMachineResults(facts, result.MachineResults, detailLevel);
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

    private static void AddMachineResults(
        List<string> facts,
        IReadOnlyList<SlackConfirmedMachineResult>? values,
        SlackFinalReplyDetailLevel detailLevel)
    {
        if (values is null)
            return;

        foreach (var value in values)
        {
            if (facts.Count == MaximumKeyResults)
                return;

            var fact = SummarizeMachineResult(value, detailLevel);
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
        var clean = NormalizeText(value);
        if (clean is null)
            return null;

        clean = SecretAssignment.Replace(clean, "[REDACTED]");
        return SlackToken.Replace(clean, "[REDACTED]");
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var clean = value.ReplaceLineEndings("\n").Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static string SummarizeMachineResult(
        SlackConfirmedMachineResult result,
        SlackFinalReplyDetailLevel detailLevel)
    {
        var label = CleanText(result.Label) ?? "Machine result";
        var payload = NormalizeText(result.Payload);
        if (payload is null)
            return $"{label}: no result";

        if (detailLevel == SlackFinalReplyDetailLevel.Diagnostic)
            return $"{label}: {CleanText(payload)}";

        if (TryParseJsonValue(payload, out var json))
            return $"{label}: {SummarizeJsonValue(json)}";

        var lines = payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3)
            .Select(CleanText)
            .Where(line => line is not null)
            .Cast<string>()
            .ToArray();
        return lines.Length == 0
            ? $"{label}: output received"
            : $"{label}: {string.Join("; ", lines)}";
    }

    private static string SummarizeJsonValue(JsonElement value, int depth = 0)
    {
        if (depth > 2)
            return "nested data";

        return value.ValueKind switch
        {
            JsonValueKind.Object => SummarizeJsonObject(value, depth),
            JsonValueKind.Array => SummarizeJsonArray(value, depth),
            JsonValueKind.String => CleanText(value.GetString()) ?? "empty text",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "no value",
            _ => "structured result received",
        };
    }

    private static string SummarizeJsonObject(JsonElement value, int depth)
    {
        var fields = value.EnumerateObject()
            .Where(property => !IsHiddenMachineProperty(property.Name))
            .Select(property =>
            {
                var propertyName = CleanText(property.Name);
                return propertyName is null
                    ? null
                    : $"{propertyName}={SummarizeJsonValue(property.Value, depth + 1)}";
            })
            .Where(field => field is not null)
            .Cast<string>()
            .Take(3)
            .ToArray();

        return fields.Length == 0
            ? "structured result received"
            : $"object: {string.Join("; ", fields)}";
    }

    private static string SummarizeJsonArray(JsonElement value, int depth)
    {
        var items = value.EnumerateArray()
            .Take(3)
            .Select(item => SummarizeJsonValue(item, depth + 1))
            .ToArray();
        var count = value.GetArrayLength();
        if (count == 0)
            return "empty list";

        var prefix = $"{count} item{(count == 1 ? "" : "s")}";
        return items.Length == 0
            ? prefix
            : $"{prefix}: {string.Join("; ", items)}";
    }

    private static bool IsHiddenMachineProperty(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "tool" or "reasoning" or "thought" or "trace" or "raw" or "stdout" or "stderr" or "log" or "logs"
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.EndsWith("id", StringComparison.Ordinal);
    }

    private static bool TryParseJsonValue(string value, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
        }

        for (var start = 0; start < value.Length; start++)
        {
            if (value[start] is not ('{' or '[')
                || !TryFindJsonEnd(value, start, out var end))
                continue;

            try
            {
                using var document = JsonDocument.Parse(value[start..end]);
                element = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
            }
        }

        element = default;
        return false;
    }

    private static bool TryFindJsonEnd(string value, int start, out int end)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character is '{' or '[')
            {
                stack.Push(character);
                continue;
            }

            if (character is not ('}' or ']'))
                continue;

            if (stack.Count == 0 || !IsMatchingJsonDelimiter(stack.Pop(), character))
            {
                end = 0;
                return false;
            }

            if (stack.Count == 0)
            {
                end = index + 1;
                return true;
            }
        }

        end = 0;
        return false;
    }

    private static bool IsMatchingJsonDelimiter(char opening, char closing) =>
        opening == '{' ? closing == '}' : closing == ']';

    private static string NeutralizeSlackControlSyntax(string value) =>
        SlackControlSyntax.Replace(value, match => $"&lt;{match.Value[1..^1]}&gt;");

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

                if (RuneCount(current.ToString()) + 1 + RuneCount(part) <= maximumLength)
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

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static int CharIndexAfterRunes(string value, int runeCount)
    {
        if (runeCount <= 0)
            return 0;

        var charIndex = 0;
        var seen = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            charIndex += rune.Utf16SequenceLength;
            seen++;
            if (seen == runeCount)
                return charIndex;
        }

        return value.Length;
    }

    private static int LastWhitespaceBoundary(string value, int maximumRunes)
    {
        var charIndex = 0;
        var seen = 0;
        var lastBoundary = -1;
        foreach (var rune in value.EnumerateRunes())
        {
            if (seen == maximumRunes)
                break;

            charIndex += rune.Utf16SequenceLength;
            seen++;
            if (Rune.IsWhiteSpace(rune))
                lastBoundary = charIndex;
        }

        return lastBoundary;
    }

    private static IEnumerable<string> SplitLine(string line, int maximumLength)
    {
        var remaining = line;
        while (RuneCount(remaining) > maximumLength)
        {
            var splitAt = LastWhitespaceBoundary(remaining, maximumLength);
            if (splitAt < 1)
                splitAt = CharIndexAfterRunes(remaining, maximumLength);

            yield return remaining[..splitAt].TrimEnd();
            remaining = remaining[splitAt..].TrimStart();
        }

        if (RuneCount(remaining) > 0)
            yield return remaining;
    }
}
