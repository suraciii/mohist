using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Runner;

public static partial class TemplateRenderer
{
    public static Dictionary<string, JsonElement?>? Render(
        Dictionary<string, JsonElement?>? input,
        Dictionary<string, JsonElement?>? variables)
    {
        if (input is null) return null;

        var rendered = new Dictionary<string, JsonElement?>();
        foreach (var (key, value) in input)
            rendered[key] = value is null ? null : RenderElement(value.Value, variables);

        return rendered;
    }

    private static JsonElement RenderElement(JsonElement element, Dictionary<string, JsonElement?>? variables)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => RenderString(element.GetString() ?? "", variables),
            JsonValueKind.Object => RenderObject(element, variables),
            JsonValueKind.Array => RenderArray(element, variables),
            _ => element.Clone(),
        };
    }

    private static JsonElement RenderObject(JsonElement element, Dictionary<string, JsonElement?>? variables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                RenderElement(property.Value, variables).WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement RenderArray(JsonElement element, Dictionary<string, JsonElement?>? variables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                RenderElement(item, variables).WriteTo(writer);
            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement RenderString(string value, Dictionary<string, JsonElement?>? variables)
    {
        var full = FullExpression().Match(value);
        if (full.Success)
            return Resolve(variables, full.Groups[1].Value) ?? JsonSerializer.SerializeToElement("");

        var rendered = InlineExpression().Replace(value, match =>
        {
            var resolved = Resolve(variables, match.Groups[1].Value);
            return resolved is null ? "" : ToTemplateString(resolved.Value);
        });

        return JsonSerializer.SerializeToElement(rendered);
    }

    private static JsonElement? Resolve(Dictionary<string, JsonElement?>? variables, string path)
    {
        if (variables is null) return null;

        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !variables.TryGetValue(parts[0], out var current) || current is null)
            return null;

        var element = current.Value;
        for (var i = 1; i < parts.Length; i++)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(parts[i], out element))
                return null;
        }

        return element.ValueKind == JsonValueKind.Null ? null : element.Clone();
    }

    private static string ToTemplateString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => value.ToString(),
    };

    [GeneratedRegex("^\\s*\\$\\{\\{\\s*([A-Za-z_][A-Za-z0-9_-]*(?:\\.[A-Za-z_][A-Za-z0-9_-]*)*)\\s*\\}\\}\\s*$")]
    private static partial Regex FullExpression();

    [GeneratedRegex("\\$\\{\\{\\s*([A-Za-z_][A-Za-z0-9_-]*(?:\\.[A-Za-z_][A-Za-z0-9_-]*)*)\\s*\\}\\}")]
    private static partial Regex InlineExpression();
}
