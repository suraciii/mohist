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
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new UnknownFailureReasonJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        }
    };

    public static readonly JsonSerializerOptions Indented = CloneIndented(Options);

    /// <summary>
    /// Options for the public external Agent API payloads (the public
    /// execution read shape and its public event payloads). Distinct
    /// contract from <see cref="Options"/>: property names are pinned
    /// by <c>JsonPropertyName</c> attributes, every allowlisted key is
    /// written — explicit nulls included, never ignored — and
    /// timestamps are written as RFC 3339 UTC instants ending in
    /// <c>Z</c>.
    /// </summary>
    public static readonly JsonSerializerOptions PublicApi = CreatePublicApiOptions();

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

    private static JsonSerializerOptions CreatePublicApiOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    /// <summary>
    /// Writes every timestamp as its UTC instant (<c>…Z</c>) so the
    /// public contract is one stable RFC 3339 shape regardless of the
    /// offset a canonical fact happened to carry.
    /// </summary>
    public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            DateTimeOffset.Parse(
                reader.GetString() ?? string.Empty,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("O"));
    }

    /// <summary>
    /// Options for deserializing wire-format payloads that tolerate
    /// comments and trailing commas (OTLP HTTP/JSON, etc.). Derived from
    /// <see cref="Options"/> so all shared converters (e.g.
    /// <see cref="UnknownFailureReasonJsonConverter"/>) are inherited.
    /// </summary>
    public static JsonSerializerOptions TolerantWireFormatOptions()
    {
        var options = new JsonSerializerOptions(Options)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return options;
    }

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
