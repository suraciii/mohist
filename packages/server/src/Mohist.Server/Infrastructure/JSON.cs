using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure;

public static class JSON
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters =
        {
            new UnknownFailureReasonJsonConverter(),
            new JsonStringEnumConverter(),
        }
    };

    public static readonly JsonSerializerOptions Indented = CloneIndented(Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Options);

    public static T DeserializeOrThrow<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)!;

    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static JsonElement SerializeToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static JsonElement DeserializeElement(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json, Options);

    public static string SerializeIndented<T>(T value) => JsonSerializer.Serialize(value, Indented);

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

    private static JsonSerializerOptions CloneIndented(JsonSerializerOptions source) =>
        new(source) { WriteIndented = true };

    internal sealed class UnknownFailureReasonJsonConverter : JsonConverter<FailureReason>
    {
        public override FailureReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString();
                return Enum.TryParse<FailureReason>(value, ignoreCase: true, out var reason)
                    ? reason
                    : FailureReason.TaskFailed;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
            {
                return Enum.IsDefined(typeof(FailureReason), numeric)
                    ? (FailureReason)numeric
                    : FailureReason.TaskFailed;
            }

            return FailureReason.TaskFailed;
        }

        public override void Write(Utf8JsonWriter writer, FailureReason value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
