using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Server.Workflow.Prompts.Infrastructure;

public sealed class PromptTemplateEngine
{
    public const int MaxPasses = 5;

    private static readonly Regex TokenRegex = new(
        @"\$\{\{\s*(?<path>[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}",
        RegexOptions.Compiled);

    public (string Rendered, IReadOnlyList<string> MissingVariables, int Depth) Render(
        string body,
        JsonElement variables)
    {
        ArgumentNullException.ThrowIfNull(body);

        var current = body;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var depth = 0;

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var didChange = false;
            var next = TokenRegex.Replace(current, match =>
            {
                var path = match.Groups["path"].Value;
                if (TryResolve(variables, path, out var resolved))
                {
                    var replacement = Stringify(resolved.GetValueOrDefault());
                    if (!string.Equals(replacement, match.Value, StringComparison.Ordinal))
                    {
                        didChange = true;
                    }
                    return replacement;
                }

                missing.Add(path);
                return match.Value;
            });

            if (!didChange) break;
            current = next;
            depth++;
        }

        foreach (Match match in TokenRegex.Matches(current))
        {
            missing.Add(match.Groups["path"].Value);
        }

        return (current, missing.ToList(), depth);
    }

    public static IReadOnlyList<string> ExtractVariables(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in TokenRegex.Matches(body))
        {
            paths.Add(match.Groups["path"].Value);
        }

        return paths.ToList();
    }

    private static bool TryResolve(JsonElement variables, string path, out JsonElement? value)
    {
        value = null;

        var current = variables;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object)
                return false;
            if (!current.TryGetProperty(part, out var next))
                return false;
            current = next;
        }

        value = current;
        return true;
    }

    private static string Stringify(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => "null",
            JsonValueKind.Number
                or JsonValueKind.True
                or JsonValueKind.False
                or JsonValueKind.Object
                or JsonValueKind.Array => value.GetRawText(),
            _ => string.Empty,
        };
    }
}
