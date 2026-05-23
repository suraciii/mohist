using System.Text.Json;

namespace Mohist.Runner.Actions;

public static class JsonInputs
{
    public static string? String(Dictionary<string, JsonElement?>? input, string key)
    {
        if (input is null || !input.TryGetValue(key, out var value) || value is null)
            return null;

        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : value.Value.ToString();
    }

    public static int? Int(Dictionary<string, JsonElement?>? input, string key)
    {
        if (input is null || !input.TryGetValue(key, out var value) || value is null)
            return null;

        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var intValue))
            return intValue;

        return int.TryParse(value.Value.ToString(), out intValue) ? intValue : null;
    }

    public static JsonElement? Element(Dictionary<string, JsonElement?>? input, string key)
    {
        if (input is null || !input.TryGetValue(key, out var value))
            return null;

        return value;
    }
}
