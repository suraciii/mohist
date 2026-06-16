namespace Mohist.Cli;

internal static class FrontmatterParser
{
    private const string Delimiter = "---";

    public abstract record Result
    {
        private Result() { }

        public sealed record Parsed(
            string Body,
            string? RecommendedWorkflow,
            string? RecommendedWorkflowReason,
            string? Risk) : Result;

        public sealed record NotFound(string Body) : Result;

        public sealed record Malformed(string Body) : Result;
    }

    public static Result Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new Result.NotFound(text ?? string.Empty);

        var lines = text.Split('\n');
        if (!IsDelimiter(StripTrailing(StripBom(lines[0]))))
            return new Result.NotFound(text);

        var closingIndex = FindClosingDelimiter(lines);
        if (closingIndex == -1)
            return new Result.Malformed(text);

        var frontmatter = Slice(lines, 1, closingIndex);
        var fields = ParseFields(frontmatter);
        if (fields.Malformed)
            return new Result.Malformed(text);

        return new Result.Parsed(
            BodyAfter(text, lines, closingIndex),
            fields.RecommendedWorkflow,
            fields.RecommendedWorkflowReason,
            fields.Risk);
    }

    private static int FindClosingDelimiter(string[] lines)
    {
        for (var i = 1; i < lines.Length; i++)
        {
            if (IsDelimiter(StripTrailing(lines[i])))
                return i;
        }
        return -1;
    }

    private sealed record Fields(
        string? RecommendedWorkflow,
        string? RecommendedWorkflowReason,
        string? Risk,
        bool Malformed);

    private static Fields ParseFields(string[] lines)
    {
        string? workflow = null;
        string? reason = null;
        string? risk = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = StripTrailing(lines[i]);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var colon = raw.IndexOf(':');
            if (colon < 0)
                return new Fields(null, null, null, Malformed: true);

            var key = raw.Substring(0, colon).Trim();
            if (key.Length == 0)
                return new Fields(null, null, null, Malformed: true);

            var rawValue = raw.Substring(colon + 1).Trim();
            string value = rawValue is "|" or ">"
                ? ReadBlock(lines, ref i, folded: rawValue == ">")
                : Unquote(rawValue);

            switch (key)
            {
                case "recommended_workflow":
                    workflow = NullIfEmpty(value);
                    break;
                case "recommended_workflow_reason":
                    reason = NullIfEmpty(value);
                    break;
                case "risk":
                    risk = NullIfEmpty(value);
                    break;
            }
        }

        return new Fields(workflow, reason, risk, Malformed: false);
    }

    private static string ReadBlock(string[] lines, ref int index, bool folded)
    {
        var collected = new List<string>();
        var indent = -1;
        var k = index + 1;
        var lastConsumed = index;

        while (k < lines.Length)
        {
            var line = StripTrailing(lines[k]);
            if (line.Length == 0)
            {
                collected.Add(string.Empty);
                lastConsumed = k;
                k++;
                continue;
            }

            var leading = LeadingWhitespace(line);
            if (leading == 0)
                break;

            indent = indent < 0 ? leading : indent;
            collected.Add(StripIndent(line, indent));
            lastConsumed = k;
            k++;
        }

        while (collected.Count > 0 && collected[^1].Length == 0)
            collected.RemoveAt(collected.Count - 1);

        index = lastConsumed;

        return folded
            ? string.Join(" ", collected.Where(l => l.Length > 0))
            : string.Join("\n", collected);
    }

    private static string BodyAfter(string text, string[] lines, int closingIndex)
    {
        var bodyStartLine = closingIndex + 1;
        var offset = 0;
        for (var i = 0; i < bodyStartLine && i < lines.Length; i++)
            offset += lines[i].Length + 1;

        return offset >= text.Length ? string.Empty : text.Substring(offset);
    }

    private static string[] Slice(string[] lines, int from, int to)
    {
        var copy = new string[to - from];
        for (var i = 0; i < copy.Length; i++)
            copy[i] = lines[from + i];
        return copy;
    }

    private static bool IsDelimiter(string line) => line == Delimiter;

    private static string StripTrailing(string line) =>
        line.EndsWith('\r') ? line[..^1] : line;

    private static string StripBom(string line) =>
        line.StartsWith('\uFEFF') ? line[1..] : line;

    private static int LeadingWhitespace(string line)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == ' ' || c == '\t') count++;
            else break;
        }
        return count;
    }

    private static string StripIndent(string line, int count) =>
        line.Length <= count ? string.Empty : line[count..];

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return value[1..^1];
        }
        return value;
    }

    private static string? NullIfEmpty(string value) =>
        value.Length == 0 ? null : value;
}
