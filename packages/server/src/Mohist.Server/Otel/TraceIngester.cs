using System.Collections.Immutable;
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
    private readonly RuntimeObservability? _runtime;
    private readonly IIngestProtectionDecision _protection;
    private readonly IOtlpIngestGate? _gate;
    private readonly OtlpWriteBlockPlanner _planner;
    private readonly Action? _transactionStarted;

    /// <summary>OTLP-defined service.name attribute key.</summary>
    public const string ServiceNameAttributeKey = "service.name";

    public TraceIngester(OtelDb db, ILogger<TraceIngester> logger)
        : this(db, logger, null, null, null, null)
    {
    }

    public TraceIngester(
        OtelDb db,
        ILogger<TraceIngester> logger,
        RuntimeObservability? runtime,
        IIngestProtectionDecision? protection = null,
        Action? transactionStarted = null,
        IOtlpIngestGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _logger = logger;
        _runtime = runtime;
        _protection = protection ?? new AcceptAllIngestProtectionDecision();
        _transactionStarted = transactionStarted;
        _gate = gate;
        _planner = new OtlpWriteBlockPlanner();
    }

    /// <summary>
    /// Test seam that exposes the protected block planner so unit
    /// tests can exercise partitioning without round-tripping
    /// through the database. Production code paths route through
    /// <see cref="IngestBatch(OtlpTraceRequest?, CancellationToken)"/>.
    /// </summary>
    internal OtlpWriteBlockPlanner Planner => _planner;

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
        var outcome = IngestBatch(request, ct);
        if (outcome.IsRetryable)
            throw new IngestStorageException(outcome.WriteResult.Reason);
        return checked((int)outcome.Saved);
    }

    internal IngestOutcome IngestBatch(OtlpTraceRequest? request, CancellationToken ct = default)
    {
        var prepared = Prepare(request);
        var plan = _planner.Plan(prepared);
        var emptyClassification = new ClassifiedBatchTotals(
            plan.TotalAccepted,
            plan.ProtectionRejected,
            plan.MalformedDropped + plan.OversizedDropped,
            0);

        if (plan.Blocks.Length == 0)
        {
            var empty = IngestOutcomeBuilder.Build(emptyClassification, IngestWriteResult.NotAttempted());
            _runtime?.RecordIngest(empty);
            return empty;
        }

        return IngestPlannedAsync(plan, ct).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async-only ingestion path used by the request loop. The
    /// synchronous overload delegates here by blocking on the
    /// returned task; production ingestion is server-initiated and
    /// synchronous, so the wrapper is the only caller.
    /// </summary>
    internal async Task<IngestOutcome> IngestPlannedAsync(OtlpWritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Blocks.Length == 0)
        {
            var emptyClassification = new ClassifiedBatchTotals(
                plan.TotalAccepted,
                plan.ProtectionRejected,
                plan.MalformedDropped + plan.OversizedDropped,
                0);
            var empty = IngestOutcomeBuilder.Build(emptyClassification, IngestWriteResult.NotAttempted());
            _runtime?.RecordIngest(empty);
            return empty;
        }

        var writerLease = _gate is null
            ? new OtlpWriterLease()
            : await _gate.AcquireWriterLeaseAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                foreach (var block in plan.Blocks)
                {
                    ct.ThrowIfCancellationRequested();
                    CommitBlock(block, ct);
                }

                var finalClassification = new ClassifiedBatchTotals(
                    plan.TotalAccepted,
                    plan.ProtectionRejected,
                    plan.MalformedDropped + plan.OversizedDropped,
                    0);
                var committed = IngestOutcomeBuilder.Build(finalClassification, IngestWriteResult.Committed());
                _runtime?.RecordIngest(committed);
                return committed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var cancelledClassification = new ClassifiedBatchTotals(
                    plan.TotalAccepted,
                    plan.ProtectionRejected,
                    plan.MalformedDropped + plan.OversizedDropped,
                    0);
                var cancelled = IngestOutcomeBuilder.Build(cancelledClassification, IngestWriteResult.Cancelled());
                _runtime?.RecordIngest(cancelled);
                throw;
            }
            catch (Exception ex)
            {
                var rolledBackClassification = new ClassifiedBatchTotals(
                    plan.TotalAccepted,
                    plan.ProtectionRejected,
                    plan.MalformedDropped + plan.OversizedDropped,
                    0);
                var rolledBack = IngestOutcomeBuilder.Build(
                    rolledBackClassification,
                    IngestWriteResult.RolledBack(ex.Message));
                _runtime?.RecordIngest(rolledBack);
                return rolledBack;
            }
        }
        finally
        {
            writerLease.Dispose();
        }
    }

    private void CommitBlock(OtlpWriteBlock block, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.OpenReadWriteConnection();
        using var transaction = connection.BeginTransaction();
        _transactionStarted?.Invoke();
        try
        {
            UpsertBlock(connection, transaction, block, ct);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void UpsertBlock(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        OtlpWriteBlock block,
        CancellationToken ct)
    {
        var spans = block.Spans;

        // Assign a single service-name per (trace_id) — the first
        // received one wins, so subsequent same-Trace duplicates
        // preserve the originally stored service name. The grouping
        // also lets us fetch existing service names in batches
        // instead of one round-trip per Span.
        var grouped = new Dictionary<string, GroupedTraceBatch>(StringComparer.Ordinal);
        foreach (var prepared in spans)
        {
            if (!grouped.TryGetValue(prepared.Span.TraceId, out var group))
            {
                group = new GroupedTraceBatch(prepared.ServiceName);
                grouped[prepared.Span.TraceId] = group;
            }
            group.IdentityBySpan[prepared.Span.SpanId] = prepared;
        }

        // Fetch existing (trace_id, span_id) identities so the count
        // delta only grows for new identities. Existing identities
        // are still replaced by the later incoming attributes.
        var identities = new List<string>(spans.Length);
        foreach (var prepared in spans)
            identities.Add($"{prepared.Span.TraceId}\u0001{prepared.Span.SpanId}");

        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            var placeholders = new string[spans.Length];
            for (var i = 0; i < spans.Length; i++)
                placeholders[i] = $"($t{i}, $s{i})";
            cmd.CommandText = $"""
                SELECT {OtelDb.SpansTraceIdColumn}, {OtelDb.SpansSpanIdColumn}
                FROM {OtelDb.SpansTable}
                WHERE ({OtelDb.SpansTraceIdColumn}, {OtelDb.SpansSpanIdColumn}) IN ({string.Join(", ", placeholders)});
                """;
            for (var i = 0; i < spans.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$t{i}", spans[i].Span.TraceId);
                cmd.Parameters.AddWithValue($"$s{i}", spans[i].Span.SpanId);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existing.Add($"{reader.GetString(0)}\u0001{reader.GetString(1)}");
        }

        // Replace each Span row. INSERT OR REPLACE preserves the
        // deterministic-truth contract: a later received row wins
        // for the same identity, so corrections self-heal.
        foreach (var prepared in spans)
        {
            ct.ThrowIfCancellationRequested();
            UpsertSpanRow(connection, transaction, prepared.Span, prepared.ServiceName, prepared.ResourceAttributesJson);
        }

        // Refresh each affected Trace once. The Trace header's
        // span_count grows by the number of *new* identities in this
        // block; bounds use indexed ORDER BY ... LIMIT 1 reads so
        // the work is bounded by the distinct affected Traces rather
        // than by the incoming Span count.
        foreach (var (traceId, group) in grouped)
        {
            ct.ThrowIfCancellationRequested();
            var newIdentityCount = 0;
            foreach (var prepared in group.IdentityBySpan.Values)
            {
                if (!existing.Contains($"{prepared.Span.TraceId}\u0001{prepared.Span.SpanId}"))
                    newIdentityCount++;
            }

            UpsertTraceHeader(
                connection,
                transaction,
                traceId,
                group.ServiceName,
                newIdentityCount);
        }
    }

    private static void UpsertSpanRow(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        NormalizedIngestSpan span,
        string serviceName,
        string? resourceAttributesJson)
    {
        using var cmd = connection.CreateCommand();
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

    private static void UpsertTraceHeader(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string traceId,
        string serviceName,
        int newIdentityCount)
    {
        // An existing trace keeps its first-stored service name and
        // absorbs a new identity count via a single-row UPDATE.
        // A new trace is initialized with the first encountered
        // service name and the same indexes give the starting bounds.
        // The deterministic ORDER BY ... LIMIT 1 reads use the
        // additive (trace_id, start_time) / (trace_id, end_time)
        // indexes, so the work grows with the affected Trace count
        // rather than the incoming Span count.
        if (TraceHeaderExists(connection, transaction, traceId))
        {
            UpdateTraceHeaderBoundsAndCount(connection, transaction, traceId, newIdentityCount);
        }
        else
        {
            InsertTraceHeader(connection, transaction, traceId, serviceName, newIdentityCount);
        }
    }

    private static bool TraceHeaderExists(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string traceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT 1 FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $trace_id";
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        var result = cmd.ExecuteScalar();
        return result is not null;
    }

    private static void InsertTraceHeader(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string traceId,
        string serviceName,
        int initialCount)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            INSERT INTO {OtelDb.TracesTable} (
                {OtelDb.TracesTraceIdColumn},
                {OtelDb.TracesServiceNameColumn},
                {OtelDb.TracesStartTimeColumn},
                {OtelDb.TracesEndTimeColumn},
                {OtelDb.TracesSpanCountColumn}
            ) VALUES (
                $trace_id, $service_name,
                (SELECT {OtelDb.SpansStartTimeColumn} FROM {OtelDb.SpansTable}
                    WHERE {OtelDb.SpansTraceIdColumn} = $trace_id
                    ORDER BY {OtelDb.SpansStartTimeColumn} ASC LIMIT 1),
                (SELECT {OtelDb.SpansEndTimeColumn} FROM {OtelDb.SpansTable}
                    WHERE {OtelDb.SpansTraceIdColumn} = $trace_id
                    ORDER BY {OtelDb.SpansEndTimeColumn} DESC LIMIT 1),
                $initial_count
            );
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$service_name", serviceName);
        cmd.Parameters.AddWithValue("$initial_count", initialCount);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateTraceHeaderBoundsAndCount(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string traceId,
        int newIdentityCount)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            UPDATE {OtelDb.TracesTable}
            SET
                {OtelDb.TracesEndTimeColumn} = (
                    SELECT {OtelDb.SpansEndTimeColumn} FROM {OtelDb.SpansTable}
                    WHERE {OtelDb.SpansTraceIdColumn} = $trace_id
                    ORDER BY {OtelDb.SpansEndTimeColumn} DESC LIMIT 1
                ),
                {OtelDb.TracesStartTimeColumn} = (
                    SELECT {OtelDb.SpansStartTimeColumn} FROM {OtelDb.SpansTable}
                    WHERE {OtelDb.SpansTraceIdColumn} = $trace_id
                    ORDER BY {OtelDb.SpansStartTimeColumn} ASC LIMIT 1
                ),
                {OtelDb.TracesSpanCountColumn} = {OtelDb.TracesSpanCountColumn} + $new_count
            WHERE {OtelDb.TracesTraceIdColumn} = $trace_id;
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$new_count", newIdentityCount);
        cmd.ExecuteNonQuery();
    }

    private sealed class GroupedTraceBatch
    {
        public GroupedTraceBatch(string serviceName)
        {
            ServiceName = serviceName;
            IdentityBySpan = new Dictionary<string, PreparedIngestSpan>(StringComparer.Ordinal);
        }

        public string ServiceName { get; }
        public Dictionary<string, PreparedIngestSpan> IdentityBySpan { get; }
    }

    public PreparedIngestBatch Prepare(OtlpTraceRequest? request)
    {
        if (request?.ResourceSpans is null || request.ResourceSpans.Count == 0)
            return new PreparedIngestBatch(ImmutableArray<PreparedIngestSpan>.Empty, 0, 0, 0);

        var parsed = new List<PreparedIngestSpan>();
        long rejected = 0;
        long malformed = 0;
        foreach (var rs in request.ResourceSpans)
        {
            var resourceAttributes = rs.Resource?.Attributes ?? new List<KeyValue>();
            var serviceName = ExtractServiceName(resourceAttributes) ?? "unknown_service";
            var resourceAttributesJson = SerializeKeyValues(resourceAttributes);
            if (rs.ScopeSpans is null)
                continue;
            foreach (var scope in rs.ScopeSpans)
            {
                if (scope.Spans is null)
                    continue;
                foreach (var span in scope.Spans)
                {
                    if (!TryNormalizeSpan(span, out var normalized))
                    {
                        malformed++;
                        _logger.LogDebug("Skipping span missing required fields (traceId/spanId/name/start/end).");
                        continue;
                    }

                    var prepared = new PreparedIngestSpan(normalized, serviceName, resourceAttributesJson);
                    if (_protection.Decide(prepared).Accepted)
                        parsed.Add(prepared);
                    else
                        rejected++;
                }
            }
        }
        return new PreparedIngestBatch(parsed.ToImmutableArray(), rejected, malformed, 0);
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

    private static bool TryNormalizeSpan(Span span, out NormalizedIngestSpan normalized)
    {
        normalized = default;

        if (string.IsNullOrEmpty(span.TraceId)
            || string.IsNullOrEmpty(span.SpanId)
            || string.IsNullOrEmpty(span.Name))
            return false;

        if (!TryConvertUnixNanoToUtcIso(span.StartTimeUnixNano, out var startIso)
            || !TryConvertUnixNanoToUtcIso(span.EndTimeUnixNano, out var endIso))
            return false;

        normalized = new NormalizedIngestSpan(
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

    internal static bool TryConvertUnixNanoToUtcIso(string? raw, out string iso)
    {
        iso = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (!long.TryParse(raw, out var nanos))
            return false;

        var seconds = nanos / 1_000_000_000L;
        var remainder = nanos % 1_000_000_000L;
        if (remainder < 0)
        {
            seconds -= 1;
            remainder += 1_000_000_000L;
        }
        var ticks = remainder / 100;
        DateTimeOffset instant;
        try
        {
            instant = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
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

}

public sealed class IngestStorageException : Exception
{
    public IngestStorageException(string? reason)
        : base(reason ?? "OTLP storage write failed.")
    {
    }
}
