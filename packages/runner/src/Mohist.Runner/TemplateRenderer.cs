using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Runner;

public static partial class TemplateRenderer
{
    public static Dictionary<string, JsonElement?>? Render(
        Dictionary<string, JsonElement?>? input,
        Dictionary<string, JsonElement?>? variables)
    {
        return Render(input, new VariableBag(variables));
    }

    public static Dictionary<string, JsonElement?>? Render(
        Dictionary<string, JsonElement?>? input,
        VariableBag variables)
    {
        if (input is null) return null;

        var rendered = new Dictionary<string, JsonElement?>();
        foreach (var (key, value) in input)
            rendered[key] = value is null ? null : RenderElement(value.Value, variables);

        return rendered;
    }

    private static JsonElement RenderElement(JsonElement element, VariableBag variables)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => RenderString(element.GetString() ?? "", variables),
            JsonValueKind.Object => RenderObject(element, variables),
            JsonValueKind.Array => RenderArray(element, variables),
            _ => element.Clone(),
        };
    }

    private static JsonElement RenderObject(JsonElement element, VariableBag variables)
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

    private static JsonElement RenderArray(JsonElement element, VariableBag variables)
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

    private static JsonElement RenderString(string value, VariableBag variables)
    {
        var full = FullExpression().Match(value);
        if (full.Success)
            return ResolveRequired(variables, full.Groups[1].Value);

        var rendered = InlineExpression().Replace(value, match =>
        {
            var resolved = ResolveRequired(variables, match.Groups[1].Value);
            return ToTemplateString(resolved);
        });

        return JsonSerializer.SerializeToElement(rendered);
    }

    private static JsonElement ResolveRequired(VariableBag variables, string path)
    {
        return variables.Element(path)
            ?? throw new InvalidOperationException($"Template variable '{path}' was not found");
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
