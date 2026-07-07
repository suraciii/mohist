using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

internal static class TaskWithExpander
{
    private static readonly Regex WholeTemplateTokenRegex = new(
        @"^\s*\$\{\{\s*(?<path>[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$",
        RegexOptions.Compiled);

    public static Dictionary<string, JsonElement?>? Expand(
        VariableBundle? effectiveVars,
        Dictionary<string, JsonElement?>? taskWith)
    {
        if (taskWith is null || taskWith.Count == 0) return taskWith;
        if (effectiveVars?.Vars is null || effectiveVars.Vars.Value.ValueKind != JsonValueKind.Object) return taskWith;

        using var varsDoc = JsonDocument.Parse(effectiveVars.Vars.Value.GetRawText());
        var varsRoot = varsDoc.RootElement;

        var result = new Dictionary<string, JsonElement?>(taskWith.Count, StringComparer.Ordinal);

        foreach (var (key, value) in taskWith)
        {
            if (!value.HasValue)
            {
                result[key] = value;
                continue;
            }

            var v = value.Value;
            if (TryResolveWholeTemplate(v, varsRoot, out var resolvedValue))
            {
                result[key] = resolvedValue.Clone();
                continue;
            }

            if (v.ValueKind == JsonValueKind.Object
                && varsRoot.ValueKind == JsonValueKind.Object
                && varsRoot.TryGetProperty(key, out var varsOverride)
                && varsOverride.ValueKind == JsonValueKind.Object)
            {
                result[key] = VariableBundle.DeepMerge(v, varsOverride);
                continue;
            }

            result[key] = v.Clone();
        }

        return result;
    }

    private static bool TryResolveWholeTemplate(JsonElement value, JsonElement varsRoot, out JsonElement resolved)
    {
        resolved = default;
        if (value.ValueKind != JsonValueKind.String || varsRoot.ValueKind != JsonValueKind.Object)
            return false;

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var match = WholeTemplateTokenRegex.Match(raw);
        if (!match.Success)
            return false;

        var current = varsRoot;
        var parts = match.Groups["path"].Value.Split('.');
        var start = parts.Length > 0 && string.Equals(parts[0], "vars", StringComparison.Ordinal) ? 1 : 0;
        for (var i = start; i < parts.Length; i++)
        {
            var part = parts[i];
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
                return false;
            current = next;
        }

        resolved = current.Clone();
        return true;
    }
}
