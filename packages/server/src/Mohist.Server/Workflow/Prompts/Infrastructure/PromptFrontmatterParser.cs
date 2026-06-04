using Mohist.Server.Workflow.Prompts.Domain;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Mohist.Server.Workflow.Prompts.Infrastructure;

public static class PromptFrontmatterParser
{
    private const string Delimiter = "---";

    public static (PromptFrontmatter Frontmatter, string Body) Parse(string fileText, string key)
    {
        ArgumentNullException.ThrowIfNull(fileText);
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Prompt key is required.", nameof(key));

        if (!TrySplitFrontmatter(fileText, out var yaml, out var body))
            return (new PromptFrontmatter(), fileText);

        try
        {
            return (ParseYaml(yaml), body);
        }
        catch (YamlException ex)
        {
            throw new PromptFrontmatterParseException(
                $"Failed to parse frontmatter for prompt '{key}': {ex.Message}", ex);
        }
    }

    private static bool TrySplitFrontmatter(string fileText, out string yaml, out string body)
    {
        var lines = fileText.Replace("\r", string.Empty).Split('\n');
        if (lines.Length == 0 || lines[0] != Delimiter)
        {
            yaml = string.Empty;
            body = fileText;
            return false;
        }

        var closingIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] == Delimiter)
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
        {
            yaml = string.Empty;
            body = fileText;
            return false;
        }

        yaml = string.Join("\n", lines, 1, closingIndex - 1);
        body = string.Join("\n", lines, closingIndex + 1, lines.Length - closingIndex - 1);
        return true;
    }

    private static PromptFrontmatter ParseYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new PromptFrontmatter();

        var deserializer = new DeserializerBuilder().Build();
        var dict = deserializer.Deserialize<Dictionary<string, object?>>(yaml)
            ?? new Dictionary<string, object?>();

        return new PromptFrontmatter
        {
            Name = TryGetString(dict, "name"),
            Description = TryGetString(dict, "description") ?? string.Empty,
            Tags = TryGetStringList(dict, "tags"),
            Stage = TryGetString(dict, "stage"),
        };
    }

    private static string? TryGetString(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is null)
            return null;
        var s = value.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static IReadOnlyList<string> TryGetStringList(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is null)
            return Array.Empty<string>();

        if (value is IEnumerable<object?> items)
        {
            return items
                .OfType<object>()
                .Select(item => item.ToString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
        }

        return Array.Empty<string>();
    }
}

public sealed class PromptFrontmatterParseException : Exception
{
    public PromptFrontmatterParseException(string message) : base(message)
    {
    }

    public PromptFrontmatterParseException(string message, Exception inner) : base(message, inner)
    {
    }
}
