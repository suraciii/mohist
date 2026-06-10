using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure;

public static class JSON
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Options);

    public static T DeserializeOrThrow<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)!;

    public static string SerializeDictionary(Dictionary<string, string> dict) =>
        Serialize(dict);

    public static Dictionary<string, string> DeserializeDictionary(string json)
    {
        if (string.IsNullOrEmpty(json)) return new(StringComparer.Ordinal);
        try
        {
            return Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }
}