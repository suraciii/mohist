using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Workflow.Definition;

public static class TemplateTokens
{
    internal static readonly Regex TokenRegex = new(
        @"\$\{\{\s*(?<path>[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}",
        RegexOptions.Compiled);

    public static bool Contains(JsonElement? value)
    {
        if (!value.HasValue) return false;
        return Contains(value.Value);
    }

    public static bool Contains(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                return !string.IsNullOrEmpty(text) && TokenRegex.IsMatch(text);
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (Contains(property.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (Contains(item)) return true;
                }
                return false;
            default:
                return false;
        }
    }
}
