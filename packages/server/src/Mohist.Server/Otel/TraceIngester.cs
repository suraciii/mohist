using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Otel.OtlpJson;

namespace Mohist.Server.Otel;

/// <summary>
/// Parses an OTLP HTTP/JSON trace payload and persists the resulting
/// rows into <c>otel.db</c> using a batched
/// <c>INSERT OR REPLACE</c> strategy so that the same span arriving on
/// two requests (e.g. exporter retries, multi-batch exports) never
/// duplicates.
/// </summary>
/// <remarks>
/// Responsibilities:
/// <list type="bullet">
///   <item>Deserialization with <see cref="OtlpJsonSerializer.Options()"/>.</item>
///   <item>Nanosecond timestamp → ISO 8601 UTC text conversion.</item>
///   <item>Per-span attribute and resource-attribute serialization back
///     to JSON text for storage in <c>spans.attributes</c> /
///     <c>spans.resource_attributes</c>.</item>
///   <item>First-resource-wins service-name extraction (the v1
///     simplification documented in design.md).</item>
///   <item>Idempotent writes: <c>INSERT OR REPLACE</c> on both
///     <c>traces</c> and <c>spans</c> tables; <c>span_count</c> is
///     recomputed on each ingest so re-arriving batches self-heal.</item>
/// </list>
/// </remarks>
public sealed class TraceIngester
{
    private readonly OtelDb _db;
    private readonly ILogger<TraceIngester> _logger;

    /// <summary>OTLP-defined service.name attribute key.</summary>
    public const string ServiceNameAttributeKey = "service.name";

    public TraceIngester(OtelDb db, ILogger<TraceIngester> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Ingest one OTLP HTTP/JSON trace export payload. Returns the
    /// number of spans successfully written; counts spans per
    /// <c>resourceSpans</c> / <c>scopeSpans</c> pair so a single
    /// payload that fans out across multiple resources / scopes reports
    /// the total. Spans missing required fields (<c>traceId</c>,
    /// <c>spanId</c>, <c>name</c>, <c>startTimeUnixNano</c>,
    /// <c>endTimeUnixNano</c>) are skipped — the payload is otherwise
    /// best-effort rather than transactional.
    /// </summary>
    public int IngestJson(string payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var request = JsonSerializer.Deserialize<OtlpTraceRequest>(payload, OtlpJsonSerializer.Options());
        return Ingest(request, ct);
    }

    internal int Ingest(OtlpTraceRequest? request, CancellationToken ct = default)
    {
        if (request?.ResourceSpans is null || request.ResourceSpans.Count == 0)
            return 0;

        using var connection = _db.OpenReadWriteConnection();
        using var transaction = connection.BeginTransaction();

        var totalSpans = 0;
        var droppedSpans = 0;

        try
        {
            foreach (var rs in request.ResourceSpans)
            {
                var resourceAttributes = rs.Resource?.Attributes ?? new List<KeyValue>();
                var serviceName = ExtractServiceName(resourceAttributes)
                    ?? "unknown_service";

                var resourceAttributesJson = SerializeKeyValues(resourceAttributes);

                if (rs.ScopeSpans is null)
                    continue;

                foreach (var scope in rs.ScopeSpans)
                {
                    if (scope.Spans is null)
                        continue;

                    foreach (var span in scope.Spans)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!TryNormalizeSpan(span, out var normalized))
                        {
                            droppedSpans++;
                            _logger.LogDebug(
                                "Skipping span missing required fields (traceId/spanId/name/start/end).");
                            continue;
                        }

                        UpsertSpan(
                            connection,
                            transaction,
                            normalized,
                            serviceName,
                            resourceAttributesJson);

                        totalSpans++;
                    }
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        _logger.LogDebug(
            "Ingested {SpanCount} span(s), dropped {DroppedSpanCount} malformed span(s), across {ResourceCount} resource(s).",
            totalSpans,
            droppedSpans,
            request.ResourceSpans.Count);

        return totalSpans;
    }

    private static string? ExtractServiceName(IReadOnlyList<KeyValue> attributes)
    {
        foreach (var kv in attributes)
        {
            if (string.Equals(kv.Key, ServiceNameAttributeKey, StringComparison.Ordinal)
                && kv.Value is { Kind: AnyValueKind.String, StringValue: { Length: > 0 } s })
            {
                return s;
            }
        }
        return null;
    }

    private static bool TryNormalizeSpan(Span span, out NormalizedSpan normalized)
    {
        normalized = default;

        if (string.IsNullOrEmpty(span.TraceId)
            || string.IsNullOrEmpty(span.SpanId)
            || string.IsNullOrEmpty(span.Name))
            return false;

        if (!TryConvertUnixNanoToUtcIso(span.StartTimeUnixNano, out var startIso)
            || !TryConvertUnixNanoToUtcIso(span.EndTimeUnixNano, out var endIso))
            return false;

        normalized = new NormalizedSpan(
            TraceId: span.TraceId,
            SpanId: span.SpanId,
            ParentSpanId: string.IsNullOrEmpty(span.ParentSpanId) ? null : span.ParentSpanId,
            Name: span.Name,
            Kind: span.Kind ?? 0,
            StartTime: startIso,
            EndTime: endIso,
            StatusCode: span.Status?.Code ?? 0,
            StatusMessage: string.IsNullOrEmpty(span.Status?.Message) ? null : span.Status!.Message,
            AttributesJson: SerializeKeyValues(span.Attributes));
        return true;
    }

    private void UpsertSpan(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        NormalizedSpan span,
        string serviceName,
        string? resourceAttributesJson)
    {
        // Span upsert — primary key (trace_id, span_id) guarantees idempotency.
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT OR REPLACE INTO {OtelDb.SpansTable} (
                    {OtelDb.SpansTraceIdColumn},
                    {OtelDb.SpansSpanIdColumn},
                    {OtelDb.SpansParentSpanIdColumn},
                    {OtelDb.SpansNameColumn},
                    {OtelDb.SpansKindColumn},
                    {OtelDb.SpansStartTimeColumn},
                    {OtelDb.SpansEndTimeColumn},
                    {OtelDb.SpansAttributesColumn},
                    {OtelDb.SpansStatusCodeColumn},
                    {OtelDb.SpansStatusMessageColumn},
                    {OtelDb.SpansResourceAttributesColumn}
                ) VALUES (
                    $trace_id, $span_id, $parent_span_id, $name, $kind,
                    $start_time, $end_time, $attributes,
                    $status_code, $status_message, $resource_attributes
                );
                """;
            cmd.Parameters.AddWithValue("$trace_id", span.TraceId);
            cmd.Parameters.AddWithValue("$span_id", span.SpanId);
            cmd.Parameters.AddWithValue("$parent_span_id", (object?)span.ParentSpanId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", span.Name);
            cmd.Parameters.AddWithValue("$kind", span.Kind);
            cmd.Parameters.AddWithValue("$start_time", span.StartTime);
            cmd.Parameters.AddWithValue("$end_time", span.EndTime);
            cmd.Parameters.AddWithValue("$attributes", (object?)span.AttributesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status_code", span.StatusCode);
            cmd.Parameters.AddWithValue("$status_message", (object?)span.StatusMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$resource_attributes", (object?)resourceAttributesJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // Trace header upsert — recompute span_count and the trace-level
        // time bounds from the fresh state so retries self-heal. The
        // service_name follows first-resource-wins per design.md
        // Decision 4: when the trace already exists we keep its existing
        // service_name rather than overwriting it with the new batch's.
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {OtelDb.TracesTable} (
                    {OtelDb.TracesTraceIdColumn},
                    {OtelDb.TracesServiceNameColumn},
                    {OtelDb.TracesStartTimeColumn},
                    {OtelDb.TracesEndTimeColumn},
                    {OtelDb.TracesSpanCountColumn}
                )
                SELECT
                    s.{OtelDb.SpansTraceIdColumn},
                    $service_name,
                    MIN(s.{OtelDb.SpansStartTimeColumn}),
                    MAX(s.{OtelDb.SpansEndTimeColumn}),
                    COUNT(*)
                FROM {OtelDb.SpansTable} s
                WHERE s.{OtelDb.SpansTraceIdColumn} = $trace_id
                GROUP BY s.{OtelDb.SpansTraceIdColumn}
                ON CONFLICT({OtelDb.TracesTraceIdColumn}) DO UPDATE SET
                    {OtelDb.TracesStartTimeColumn} = MIN({OtelDb.TracesTable}.{OtelDb.TracesStartTimeColumn}, excluded.{OtelDb.TracesStartTimeColumn}),
                    {OtelDb.TracesEndTimeColumn} = MAX({OtelDb.TracesTable}.{OtelDb.TracesEndTimeColumn}, excluded.{OtelDb.TracesEndTimeColumn}),
                    {OtelDb.TracesSpanCountColumn} = (SELECT COUNT(*) FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = {OtelDb.TracesTable}.{OtelDb.TracesTraceIdColumn});
                """;
            cmd.Parameters.AddWithValue("$service_name", serviceName);
            cmd.Parameters.AddWithValue("$trace_id", span.TraceId);
            cmd.ExecuteNonQuery();
        }
    }

    internal static bool TryConvertUnixNanoToUtcIso(string? raw, out string iso)
    {
        iso = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (!long.TryParse(raw, out var nanos))
            return false;

        // OTLP timestamps are signed int64; clamp to the .NET
        // DateTimeOffset range so callers can't trip the
        // ArgumentOutOfRangeException path with pathological inputs.
        // DateTimeOffset ticks are 100ns units, so the corresponding
        // nanosecond bounds are the tick range * 100.
        const long minNanos = 0L;
        const long maxNanos = unchecked(3_155_378_975_999_999_999L * 100L) + 99L;
        if (nanos < minNanos || nanos > maxNanos)
            return false;

        var seconds = nanos / 1_000_000_000L;
        var remainder = nanos % 1_000_000_000L;
        if (remainder < 0)
        {
            // C# integer division rounds toward zero, so negative nanos
            // (pre-1970) need a correction to land on a floor-rounded
            // second.
            seconds -= 1;
            remainder += 1_000_000_000L;
        }
        var ticks = remainder / 100;
        var instant = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks);
        iso = instant.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static string? SerializeKeyValues(IReadOnlyList<KeyValue>? keyValues)
    {
        if (keyValues is null || keyValues.Count == 0)
            return null;

        var projection = new List<Dictionary<string, object?>>(keyValues.Count);
        foreach (var kv in keyValues)
        {
            if (string.IsNullOrEmpty(kv.Key))
                continue;

            projection.Add(new Dictionary<string, object?>
            {
                ["key"] = kv.Key,
                ["value"] = ProjectAnyValue(kv.Value),
            });
        }

        if (projection.Count == 0)
            return null;

        return JsonSerializer.Serialize(projection, OtlpJsonSerializer.Options());
    }

    private static object? ProjectAnyValue(AnyValue? value)
    {
        if (value is null)
            return null;

        return value.Kind switch
        {
            AnyValueKind.String => value.StringValue,
            AnyValueKind.Bool => value.BoolValue,
            AnyValueKind.Int => value.IntValue,
            AnyValueKind.Double => value.DoubleValue,
            AnyValueKind.Bytes => value.BytesValue is null ? null : Convert.ToBase64String(value.BytesValue),
            AnyValueKind.Array => value.ArrayValue?.Select(ProjectAnyValue).ToList(),
            AnyValueKind.KeyValueList => value.KvlistValue?
                .Where(kv => !string.IsNullOrEmpty(kv.Key))
                .Select(kv => new Dictionary<string, object?>
                {
                    ["key"] = kv.Key,
                    ["value"] = ProjectAnyValue(kv.Value),
                })
                .ToList(),
            _ => null,
        };
    }

    private readonly record struct NormalizedSpan(
        string TraceId,
        string SpanId,
        string? ParentSpanId,
        string Name,
        int Kind,
        string StartTime,
        string EndTime,
        int StatusCode,
        string? StatusMessage,
        string? AttributesJson);
}
