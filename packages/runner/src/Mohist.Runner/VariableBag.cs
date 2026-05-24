using System.Text.Json;

namespace Mohist.Runner;

public sealed class VariableBag
{
    private readonly Dictionary<string, JsonElement?> _values;

    public VariableBag(Dictionary<string, JsonElement?>? values = null)
    {
        _values = values is not null
            ? new Dictionary<string, JsonElement?>(values, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, JsonElement?> ToDictionary() => new(_values, StringComparer.OrdinalIgnoreCase);

    public void Set(string name, object value) => _values[name] = JsonSerializer.SerializeToElement(value);

    public string? String(string path)
    {
        var element = Element(path);
        if (element is null) return null;
        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Number => element.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => element.Value.ToString()
        };
    }

    public JsonElement? Element(string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !_values.TryGetValue(parts[0], out var current) || current is null)
            return null;

        var element = current.Value;
        for (var i = 1; i < parts.Length; i++)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(parts[i], out element))
                return null;
        }

        return element.ValueKind == JsonValueKind.Null ? null : element.Clone();
    }
}
