using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Otel.OtlpJson;

/// <summary>
/// Custom <see cref="JsonConverter{T}"/> for OTLP <c>AnyValue</c>, the
/// <c>oneof</c> union encoded in JSON as a single property name on the
/// owning object (<c>stringValue</c>, <c>boolValue</c>, <c>intValue</c>,
/// <c>doubleValue</c>, <c>arrayValue</c>, <c>kvlistValue</c>,
/// <c>bytesValue</c>).
/// </summary>
/// <remarks>
/// The OTel spec maps the wire oneof to whichever JSON property is
/// present; this converter inspects the first property it sees and
/// populates the matching <see cref="AnyValue"/> field. The
/// <see cref="AnyValue.Kind"/> field lets downstream code (the ingester
/// serialization path) re-emit the value with the original variant
/// intact.
/// </remarks>
public sealed class AnyValueConverter : JsonConverter<AnyValue>
{
    public override AnyValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new AnyValue { Kind = AnyValueKind.Null };

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected AnyValue object, got {reader.TokenType}.");

        var value = new AnyValue();
        var sawField = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (!sawField)
                    throw new JsonException("AnyValue object contained no recognized oneof field.");
                return value;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"Expected property name in AnyValue, got {reader.TokenType}.");

            var propertyName = reader.GetString();
            if (!reader.Read())
                throw new JsonException("Unexpected end of JSON inside AnyValue.");

            if (string.Equals(propertyName, "stringValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.String;
                value.StringValue = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                sawField = true;
            }
            else if (string.Equals(propertyName, "boolValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.Bool;
                value.BoolValue = reader.TokenType == JsonTokenType.Null ? null : reader.GetBoolean();
                sawField = true;
            }
            else if (string.Equals(propertyName, "intValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.Int;
                value.IntValue = ReadLong(ref reader);
                sawField = true;
            }
            else if (string.Equals(propertyName, "doubleValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.Double;
                value.DoubleValue = reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();
                sawField = true;
            }
            else if (string.Equals(propertyName, "arrayValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.Array;
                var wrapper = JsonSerializer.Deserialize<ArrayValueContainer>(ref reader, options);
                value.ArrayValue = wrapper?.Values;
                sawField = true;
            }
            else if (string.Equals(propertyName, "kvlistValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.KeyValueList;
                var kvlist = JsonSerializer.Deserialize<KeyValueListContainer>(ref reader, options);
                value.KvlistValue = kvlist?.Values;
                sawField = true;
            }
            else if (string.Equals(propertyName, "bytesValue", StringComparison.Ordinal))
            {
                value.Kind = AnyValueKind.Bytes;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    value.BytesValue = null;
                }
                else
                {
                    var raw = reader.GetBytesFromBase64();
                    value.BytesValue = raw;
                }
                sawField = true;
            }
            else
            {
                // Unknown future OTel field — ignore but still consume its
                // value so the reader advances.
                reader.Skip();
            }
        }

        throw new JsonException("Unexpected end of JSON inside AnyValue.");
    }

    public override void Write(Utf8JsonWriter writer, AnyValue value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        switch (value.Kind)
        {
            case AnyValueKind.String:
                writer.WriteString("stringValue", value.StringValue);
                break;
            case AnyValueKind.Bool:
                writer.WriteBoolean("boolValue", value.BoolValue ?? false);
                break;
            case AnyValueKind.Int:
                writer.WriteNumber("intValue", value.IntValue ?? 0L);
                break;
            case AnyValueKind.Double:
                writer.WriteNumber("doubleValue", value.DoubleValue ?? 0d);
                break;
            case AnyValueKind.Array:
                writer.WritePropertyName("arrayValue");
                JsonSerializer.Serialize(writer, new ArrayValueContainer(value.ArrayValue ?? new List<AnyValue>()), options);
                break;
            case AnyValueKind.KeyValueList:
                writer.WritePropertyName("kvlistValue");
                JsonSerializer.Serialize(writer, new KeyValueListContainer(value.KvlistValue ?? new List<KeyValue>()), options);
                break;
            case AnyValueKind.Bytes:
                if (value.BytesValue is null)
                    writer.WriteNull("bytesValue");
                else
                    writer.WriteBase64String("bytesValue", value.BytesValue);
                break;
            default:
                // Null kind — emit an empty object so downstream consumers
                // still see a valid AnyValue.
                break;
        }
        writer.WriteEndObject();
    }

    private static long? ReadLong(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt64();
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            return long.TryParse(raw, out var parsed) ? parsed : null;
        }
        throw new JsonException($"Expected integer for intValue, got {reader.TokenType}.");
    }

    /// <summary>
    /// OTLP encodes a key-value list as <c>{ "values": [...] }</c>; this
    /// wrapper lets the converter pick just the inner array without
    /// defining an extra POCO type on the wire payload.
    /// </summary>
    private sealed class KeyValueListContainer
    {
        public KeyValueListContainer() { }

        public KeyValueListContainer(List<KeyValue> values)
        {
            Values = values;
        }

        [JsonPropertyName("values")]
        public List<KeyValue> Values { get; set; } = new();
    }

    /// <summary>
    /// OTLP encodes an array value as <c>{ "values": [...] }</c>; this
    /// wrapper lets the converter pick the inner list without defining
    /// an extra POCO type on the wire payload.
    /// </summary>
    private sealed class ArrayValueContainer
    {
        public ArrayValueContainer() { }

        public ArrayValueContainer(List<AnyValue> values)
        {
            Values = values;
        }

        [JsonPropertyName("values")]
        public List<AnyValue> Values { get; set; } = new();
    }
}
