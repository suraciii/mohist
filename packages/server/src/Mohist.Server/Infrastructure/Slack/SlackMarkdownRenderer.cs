using System.Text;
using System.Text.RegularExpressions;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Converts the markdown subset Agents use in Slack replies into Slack
/// mrkdwn: <c>**bold**</c> → <c>*bold*</c>, inline code and fenced code
/// blocks pass through, list markers become bullet points, quotes pass
/// through. Unsupported markdown (tables, headings) degrades to readable
/// plain text instead of failing. Content inside code spans is never
/// converted.
/// </summary>
public static class SlackMarkdownRenderer
{
    private static readonly Regex HeadingPrefix = new(
        "^ {0,3}#{1,6}[ \\t]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FenceMarker = new(
        "^ {0,3}(?:```|~~~)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TableSeparator = new(
        "^\\s*\\|?[\\s:|-]+\\|?\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ListMarker = new(
        "^( {0,3})[-*+]([ \\t]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BoldDelimiter = new(
        @"\*\*(?!\s)([^*\r\n]+?)(?<!\s)\*\*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ToMrkdwn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var result = new List<string>(lines.Length);
        var inFence = false;
        foreach (var line in lines)
        {
            if (!inFence && FenceMarker.IsMatch(line))
            {
                inFence = true;
                result.Add(line);
                continue;
            }

            if (inFence)
            {
                if (FenceMarker.IsMatch(line))
                    inFence = false;
                result.Add(line);
                continue;
            }

            if (HeadingPrefix.IsMatch(line))
            {
                result.Add(HeadingPrefix.Replace(line, ""));
                continue;
            }

            if (IsTableSeparator(line.TrimStart()))
                continue;

            var converted = TrimTableRow(line);
            converted = ListMarker.Replace(converted, "$1•$2");
            result.Add(ConvertBold(converted));
        }

        return string.Join('\n', result);
    }

    private static bool IsTableSeparator(string trimmed) =>
        trimmed.Contains('-', StringComparison.Ordinal)
        && TableSeparator.IsMatch(trimmed);

    private static string TrimTableRow(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('|') || trimmed.IndexOf('|', 1) < 0)
            return line;

        var end = trimmed.EndsWith('|') ? trimmed.Length - 1 : trimmed.Length;
        return trimmed[1..end].Trim();
    }

    private static string ConvertBold(string line)
    {
        var result = new StringBuilder(line.Length);
        var index = 0;
        while (index < line.Length)
        {
            var backtick = line.IndexOf('`', index);
            if (backtick < 0)
            {
                result.Append(BoldDelimiter.Replace(line[index..], "*$1*"));
                break;
            }

            result.Append(BoldDelimiter.Replace(line[index..backtick], "*$1*"));
            var close = line.IndexOf('`', backtick + 1);
            if (close < 0)
            {
                result.Append(line[backtick..]);
                break;
            }

            result.Append(line.AsSpan(backtick, close - backtick + 1));
            index = close + 1;
        }

        return result.ToString();
    }
}
