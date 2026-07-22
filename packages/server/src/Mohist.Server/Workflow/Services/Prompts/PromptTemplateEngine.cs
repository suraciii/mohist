using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Workflow.Services.Prompts;

public sealed class PromptTemplateEngine : ISingletonService
{
    public const int MaxPasses = 5;
    private const string EscapeSentinel = "\u0000LITERAL_DOLLAR_BRACE\u0000";
    private static readonly HashSet<string> AllowedRoots = new(StringComparer.Ordinal)
    {
        "workflow",
        "stage",
        "work",
        "issue",
        "repository",
        "workspace",
        "vars",
        "tasks",
        "prompts",
        "failure",
    };

    private static readonly Regex TokenRegex = new(
        @"\$\{\{\s*(?<path>[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}",
        RegexOptions.Compiled);

    public TemplateRenderResult Render(
        string body,
        JsonElement variables)
    {
        ArgumentNullException.ThrowIfNull(body);

        var current = body;
        var errors = new List<TemplateRenderError>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;

        current = current.Replace("\\${{", EscapeSentinel, StringComparison.Ordinal);
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            if (!seenValues.Add(current))
            {
                AddError(errors, "cycle", null, "Template variable expansion cycle detected");
                return new TemplateRenderResult(RemoveReferences(current), Errors(errors), depth);
            }

            var didChange = false;
            var next = TokenRegex.Replace(current, match =>
            {
                var path = match.Groups["path"].Value;
                if (TryResolve(variables, path, out var resolved))
                {
                    var value = resolved!.Value;
                    if (IsCompleteExpression(current, match) && IsObjectOrArray(value))
                    {
                        AddError(errors, "invalid_type", path,
                            $"Template variable '{path}' resolves to an object or array and cannot be rendered in a prompt");
                        didChange = true;
                        return string.Empty;
                    }

                    if (!IsCompleteExpression(current, match) && IsObjectOrArray(value))
                    {
                        AddError(errors, "invalid_type", path,
                            $"Template variable '{path}' resolves to an object or array and cannot be embedded in a string");
                        didChange = true;
                        return string.Empty;
                    }

                    var replacement = Stringify(value, embedded: !IsCompleteExpression(current, match));
                    if (!string.Equals(replacement, match.Value, StringComparison.Ordinal))
                    {
                        didChange = true;
                    }
                    return replacement;
                }

                AddError(errors, "missing_reference", path, $"Template variable '{path}' was not found");
                didChange = true;
                return string.Empty;
            });

            if (!didChange)
                return new TemplateRenderResult(RestoreEscapes(next), Errors(errors), depth);

            current = next;
            depth++;
        }

        if (TokenRegex.IsMatch(current))
            AddError(errors, "max_depth", null, "Template variable expansion exceeded maximum depth");

        return new TemplateRenderResult(RemoveReferences(current), Errors(errors), depth);
    }

    public static IReadOnlyList<string> ExtractVariables(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var normalized = body.Replace("\\${{", EscapeSentinel, StringComparison.Ordinal);
        foreach (Match match in TokenRegex.Matches(normalized))
        {
            paths.Add(match.Groups["path"].Value);
        }

        return paths.ToList();
    }

    private static bool TryResolve(JsonElement variables, string path, out JsonElement? value)
    {
        value = null;

        var root = path.Split('.', 2)[0];
        if (!AllowedRoots.Contains(root))
            return false;

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

    private static string Stringify(JsonElement value, bool embedded)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => embedded ? string.Empty : "null",
            JsonValueKind.Number
                or JsonValueKind.True
                or JsonValueKind.False => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool IsCompleteExpression(string body, Match match) =>
        body.AsSpan(0, match.Index).Trim().IsEmpty &&
        body.AsSpan(match.Index + match.Length).Trim().IsEmpty;

    private static bool IsObjectOrArray(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static void AddError(List<TemplateRenderError> errors, string code, string? path, string message)
    {
        if (errors.Any(error => error.Code == code && error.Path == path)) return;
        errors.Add(new TemplateRenderError(code, path, message));
    }

    private static IReadOnlyList<TemplateRenderError> Errors(List<TemplateRenderError> errors) =>
        errors.OrderBy(error => error.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal).ToArray();

    private static string RestoreEscapes(string value) => value.Replace(EscapeSentinel, "${{", StringComparison.Ordinal);

    private static string RemoveReferences(string value) => RestoreEscapes(TokenRegex.Replace(value, string.Empty));
}

public sealed record TemplateRenderError(string Code, string? Path, string Message);

public sealed record TemplateRenderResult(
    string Rendered,
    IReadOnlyList<TemplateRenderError> Errors,
    int Depth)
{
    public IReadOnlyList<string> MissingVariables =>
        Errors.Where(error => error.Code == "missing_reference" && error.Path is not null)
            .Select(error => error.Path!)
            .ToArray();
}
