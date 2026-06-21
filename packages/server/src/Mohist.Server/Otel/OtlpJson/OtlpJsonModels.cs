using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Otel.OtlpJson;

/// <summary>
/// Root OTLP trace export payload. Mirrors the
/// <c>opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest</c>
/// schema. The HTTP/JSON encoding uses camelCase property names and
/// nullable fields; unknown fields are tolerated (newer OTLP versions add
/// fields without breaking v1 receivers).
/// </summary>
public sealed class OtlpTraceRequest
{
    /// <summary>
    /// Per-resource span batches. A single request may carry traces from
    /// multiple resources (different services within the same trace).
    /// </summary>
    [JsonPropertyName("resourceSpans")]
    public List<ResourceSpans>? ResourceSpans { get; set; }
}

/// <summary>
/// A batch of spans sharing one <see cref="Resource"/> (which carries the
/// <c>service.name</c> + other resource-level attributes).
/// </summary>
public sealed class ResourceSpans
{
    [JsonPropertyName("resource")]
    public Resource? Resource { get; set; }

    [JsonPropertyName("scopeSpans")]
    public List<ScopeSpans>? ScopeSpans { get; set; }

    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }
}

/// <summary>
/// Instrumentation scope (one library + version producing a batch of spans).
/// </summary>
public sealed class ScopeSpans
{
    [JsonPropertyName("scope")]
    public InstrumentationScope? Scope { get; set; }

    [JsonPropertyName("spans")]
    public List<Span>? Spans { get; set; }

    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }
}

/// <summary>
/// Resource attributes describing the entity producing telemetry
/// (typically <c>service.name</c>, <c>service.version</c>, etc.).
/// </summary>
public sealed class Resource
{
    [JsonPropertyName("attributes")]
    public List<KeyValue>? Attributes { get; set; }

    [JsonPropertyName("droppedAttributesCount")]
    public uint? DroppedAttributesCount { get; set; }
}

/// <summary>
/// Instrumentation scope: the library/component that produced the span.
/// </summary>
public sealed class InstrumentationScope
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("attributes")]
    public List<KeyValue>? Attributes { get; set; }

    [JsonPropertyName("droppedAttributesCount")]
    public uint? DroppedAttributesCount { get; set; }
}

/// <summary>
/// A single span as encoded by OTLP. Times are transmitted as
/// nanosecond strings; <see cref="TraceIngester"/> converts them to ISO
/// 8601 UTC text before storage.
/// </summary>
public sealed class Span
{
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public int? Kind { get; set; }

    [JsonPropertyName("startTimeUnixNano")]
    public string? StartTimeUnixNano { get; set; }

    [JsonPropertyName("endTimeUnixNano")]
    public string? EndTimeUnixNano { get; set; }

    [JsonPropertyName("attributes")]
    public List<KeyValue>? Attributes { get; set; }

    [JsonPropertyName("status")]
    public Status? Status { get; set; }

    [JsonPropertyName("traceState")]
    public string? TraceState { get; set; }
}

/// <summary>
/// A typed key/value pair. The value is a <see cref="AnyValue"/> which is
/// a JSON <c>oneof</c> — see <see cref="AnyValueConverter"/>.
/// </summary>
public sealed class KeyValue
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public AnyValue? Value { get; set; }
}

/// <summary>
/// <c>oneof</c> value cell. The active variant is whichever JSON property
/// is present (stringValue / boolValue / intValue / doubleValue /
/// arrayValue / kvlistValue / bytesValue). <see cref="Kind"/> reports
/// which variant was observed so writers can serialize it back
/// unambiguously.
/// </summary>
[JsonConverter(typeof(AnyValueConverter))]
public sealed class AnyValue
{
    public AnyValueKind Kind { get; set; }

    /// <summary>Active for <see cref="AnyValueKind.String"/>.</summary>
    public string? StringValue { get; set; }

    /// <summary>Active for <see cref="AnyValueKind.Bool"/>.</summary>
    public bool? BoolValue { get; set; }

    /// <summary>Active for <see cref="AnyValueKind.Int"/>.</summary>
    public long? IntValue { get; set; }

    /// <summary>Active for <see cref="AnyValueKind.Double"/>.</summary>
    public double? DoubleValue { get; set; }

    /// <summary>Active for <see cref="AnyValueKind.Array"/>.</summary>
    public List<AnyValue>? ArrayValue { get; set; }

    /// <summary>Active for <see cref="AnyValueKind.KeyValueList"/>.</summary>
    public List<KeyValue>? KvlistValue { get; set; }

    /// <summary>Raw bytes for <see cref="AnyValueKind.Bytes"/> (base64 string).</summary>
    public byte[]? BytesValue { get; set; }
}

/// <summary>Identifies which <c>oneof</c> variant an <see cref="AnyValue"/> carries.</summary>
public enum AnyValueKind
{
    Null = 0,
    String = 1,
    Bool = 2,
    Int = 3,
    Double = 4,
    Array = 5,
    KeyValueList = 6,
    Bytes = 7,
}

/// <summary>
/// Span status. <see cref="Code"/> follows the OTLP enum mapping
/// (0 = Unset, 1 = Ok, 2 = Error).
/// </summary>
public sealed class Status
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }
}

/// <summary>
/// Strongly-typed JSON options for deserializing OTLP trace payloads.
/// Uses camelCase (matching the OTLP HTTP/JSON spec) and ignores
/// unknown fields so newer clients can talk to a v1 receiver.
/// </summary>
public static class OtlpJsonSerializer
{
    public static JsonSerializerOptions Options() => JSON.TolerantWireFormatOptions();
}
