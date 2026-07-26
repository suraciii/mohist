using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Mohist.Server.Otel.OtlpJson;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public sealed class TraceIngesterBoundedWriteSpecs
{
    private const string TraceId = "00000000000000000000000000000001";
    private const string SpanIdPrefix = "00000000000000";
    private const string StartNanos = "1767225600000000000";
    private const string EndNanos = "1767225601000000000";

    private readonly OtelDb _db;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keeper;
    private readonly OtlpIngestGate _gate;

    public TraceIngesterBoundedWriteSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
        _gate = new OtlpIngestGate();
    }

    [Fact]
    public async Task IngestBatch_OneBlock_CommitsSpansAndHeader()
    {
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: null,
            gate: _gate);

        var request = BuildJsonRequest(TraceId, SpanIdPrefix + "01", "svc-a");
        var plan = ingester.Planner.Plan(ingester.Prepare(request));
        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.Success, outcome.ResponseDisposition);
        Assert.Equal(1, outcome.Saved);
        Assert.Equal(0, outcome.Dropped);
        Assert.Equal(0, outcome.Rejected);

        using var connection = _db.OpenReadOnlyConnection();
        AssertSpanRow(connection, TraceId, SpanIdPrefix + "01", name: "GET /x");
        AssertTraceHeader(connection, TraceId, "svc-a", spanCount: 1, startIso: "2026-01-01T00:00:00.0000000Z", endIso: "2026-01-01T00:00:01.0000000Z");
    }

    [Fact]
    public async Task IngestBatch_DuplicateSpan_ReplacesRowAndPreservesTraceCount()
    {
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: null,
            gate: _gate);

        var first = BuildJsonRequest(TraceId, SpanIdPrefix + "01", "svc-a");
        var firstOutcome = await ingester.IngestPlannedAsync(ingester.Planner.Plan(ingester.Prepare(first)));
        Assert.Equal(1, firstOutcome.Saved);

        var corrected = BuildCorrectedJsonRequest(TraceId, SpanIdPrefix + "01", "svc-a");
        var correctedOutcome = await ingester.IngestPlannedAsync(ingester.Planner.Plan(ingester.Prepare(corrected)));

        Assert.Equal(IngestResponseDisposition.Success, correctedOutcome.ResponseDisposition);
        Assert.Equal(1, correctedOutcome.Saved);

        using var connection = _db.OpenReadOnlyConnection();
        AssertSpanRow(connection, TraceId, SpanIdPrefix + "01", name: "CORRECTED");
        AssertTraceHeader(connection, TraceId, "svc-a", spanCount: 1, startIso: "2026-01-01T00:00:00.0000000Z", endIso: "2026-01-01T00:00:01.0000000Z");
    }

    [Fact]
    public async Task IngestBatch_ProtectionRejectsAllSpans_PublishesPartialSuccess()
    {
        var runtime = new RuntimeObservability(
            true,
            new RuntimeEpoch(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime,
            new RejectAllProtectionDecision(),
            transactionStarted: null,
            gate: _gate);

        var request = BuildJsonRequest(TraceId, SpanIdPrefix + "01", "svc-a");
        var plan = ingester.Planner.Plan(ingester.Prepare(request));
        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.PartialSuccess, outcome.ResponseDisposition);
        Assert.Equal(1, outcome.Rejected);
        Assert.Equal(0, outcome.Saved);

        var snapshot = runtime.GetSnapshot();
        Assert.Equal(1, snapshot.RejectedSpans);
        Assert.Equal(0, snapshot.SavedSpans);

        using var connection = _db.OpenReadOnlyConnection();
        AssertNoRows(connection, OtelDb.SpansTable);
        AssertNoRows(connection, OtelDb.TracesTable);
    }

    [Fact]
    public async Task IngestBatch_OversizedSpan_ClassifiedAsDroppedAndPersistsOthers()
    {
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: null,
            gate: _gate);

        var smallSpan = BuildJsonRequest(TraceId, SpanIdPrefix + "01", "svc-a");
        var oversizedSpan = BuildOversizedPreparedSpan(TraceId, SpanIdPrefix + "02", "svc-a");
        var smallPrep = ingester.Prepare(smallSpan);
        var combined = new PreparedIngestBatch(
            smallPrep.ParsedSpans.Add(oversizedSpan),
            0, 0, 0);
        var plan = ingester.Planner.Plan(combined);

        Assert.Equal(1, plan.OversizedDropped);

        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.PartialSuccess, outcome.ResponseDisposition);
        Assert.Equal(1, outcome.Saved);
        Assert.Equal(1, outcome.Dropped);

        using var connection = _db.OpenReadOnlyConnection();
        AssertSpanRow(connection, TraceId, SpanIdPrefix + "01", name: "GET /x");
        AssertNoSpanRow(connection, TraceId, SpanIdPrefix + "02");
        AssertTraceHeader(connection, TraceId, "svc-a", spanCount: 1, startIso: "2026-01-01T00:00:00.0000000Z", endIso: "2026-01-01T00:00:01.0000000Z");
    }

    [Fact]
    public async Task IngestBatch_HitsSpanCountLimit_SplitsAcrossMultipleBlocks()
    {
        var transactionCount = 0;
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: () => transactionCount++,
            gate: _gate);

        var traceCount = 3;
        var spansPerTrace = OtlpWriteBlockPlanner.MaxSpansPerBlock + 50;
        var prepared = new List<PreparedIngestSpan>(traceCount * spansPerTrace);
        for (var t = 0; t < traceCount; t++)
        {
            var traceId = $"0000000000000000000000000000{t:X4}0";
            for (var s = 0; s < spansPerTrace; s++)
            {
                var spanId = $"{SpanIdPrefix}{t:X2}{s:X4}";
                var span = new NormalizedIngestSpan(
                    TraceId: traceId,
                    SpanId: spanId,
                    ParentSpanId: null,
                    Name: $"s-{t}-{s}",
                    Kind: 1,
                    StartTime: "2026-01-01T00:00:00.0000000Z",
                    EndTime: "2026-01-01T00:00:01.0000000Z",
                    StatusCode: 1,
                    StatusMessage: null,
                    AttributesJson: null);
                prepared.Add(new PreparedIngestSpan(span, "svc-a", null));
            }
        }
        var batch = new PreparedIngestBatch(prepared.ToImmutableArray(), 0, 0, 0);
        var plan = ingester.Planner.Plan(batch);

        // 3 traces × 562 spans = 1686 total. With a 512-Span block
        // limit and a 1-trace-per-block ordering, the first block
        // holds 512 spans, the next three blocks each hold 512
        // spans from one trace's continuation, and the final block
        // holds the remaining 150 spans.
        Assert.Equal(4, plan.Blocks.Length);
        Assert.Equal(OtlpWriteBlockPlanner.MaxSpansPerBlock, plan.Blocks[0].Spans.Length);
        Assert.Equal(OtlpWriteBlockPlanner.MaxSpansPerBlock, plan.Blocks[1].Spans.Length);
        Assert.Equal(OtlpWriteBlockPlanner.MaxSpansPerBlock, plan.Blocks[2].Spans.Length);
        Assert.Equal(150, plan.Blocks[3].Spans.Length);

        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.Success, outcome.ResponseDisposition);
        Assert.Equal((long)plan.TotalAccepted, outcome.Saved);
        Assert.Equal(4, transactionCount);

        using var connection = _db.OpenReadOnlyConnection();
        for (var t = 0; t < traceCount; t++)
        {
            var traceId = $"0000000000000000000000000000{t:X4}0";
            AssertTraceHeader(connection, traceId, "svc-a", spansPerTrace, "2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:01.0000000Z");
        }
    }

    [Fact]
    public async Task IngestBatch_CrossBlockTraceSummary_PreservesCountBoundsAndFirstServiceName()
    {
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: null,
            gate: _gate);

        var traceId = "0000000000000000000000000000a000";
        var spansPerTrace = OtlpWriteBlockPlanner.MaxSpansPerBlock + 32;
        var prepared = new List<PreparedIngestSpan>(spansPerTrace);
        for (var s = 0; s < spansPerTrace; s++)
        {
            var spanId = $"{SpanIdPrefix}{s:X4}";
            var span = new NormalizedIngestSpan(
                TraceId: traceId,
                SpanId: spanId,
                ParentSpanId: null,
                Name: $"s-{s}",
                Kind: 1,
                StartTime: s == 0 ? "2026-01-01T00:00:00.0000000Z" : $"2026-01-01T00:00:0{(s / 60) % 10}.{s % 60:D2}000000Z",
                EndTime: s == spansPerTrace - 1
                    ? "2026-01-01T00:00:59.9999999Z"
                    : $"2026-01-01T00:00:0{(s / 60) % 10}.{(s + 1) % 60:D2}000000Z",
                StatusCode: 1,
                StatusMessage: null,
                AttributesJson: null);
            prepared.Add(new PreparedIngestSpan(span, "first-svc", null));
        }
        var batch = new PreparedIngestBatch(prepared.ToImmutableArray(), 0, 0, 0);
        var plan = ingester.Planner.Plan(batch);

        Assert.Equal(2, plan.Blocks.Length);

        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.Success, outcome.ResponseDisposition);
        Assert.Equal(spansPerTrace, outcome.Saved);

        using var connection = _db.OpenReadOnlyConnection();
        AssertTraceHeader(connection, traceId, "first-svc", spansPerTrace, "2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:59.9999999Z");
    }

    [Fact]
    public async Task IngestBatch_OperationCount_IsBoundedByAcceptedSpans()
    {
        var transactionCount = 0;
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: () => transactionCount++,
            gate: _gate);

        var traceCount = 4;
        var spansPerTrace = 200;
        var prepared = new List<PreparedIngestSpan>(traceCount * spansPerTrace);
        for (var t = 0; t < traceCount; t++)
        {
            var traceId = $"0000000000000000000000000000{(0xb000 + t):X4}";
            for (var s = 0; s < spansPerTrace; s++)
            {
                var spanId = $"{SpanIdPrefix}{t:X2}{s:X4}";
                var span = new NormalizedIngestSpan(
                    TraceId: traceId,
                    SpanId: spanId,
                    ParentSpanId: null,
                    Name: $"s-{t}-{s}",
                    Kind: 1,
                    StartTime: "2026-01-01T00:00:00.0000000Z",
                    EndTime: "2026-01-01T00:00:01.0000000Z",
                    StatusCode: 1,
                    StatusMessage: null,
                    AttributesJson: null);
                prepared.Add(new PreparedIngestSpan(span, "svc-a", null));
            }
        }
        var batch = new PreparedIngestBatch(prepared.ToImmutableArray(), 0, 0, 0);
        var plan = ingester.Planner.Plan(batch);

        // 4 traces × 200 spans = 800 spans. The block plan keeps
        // a trace per block when each trace has fewer Spans than
        // the block limit, so this becomes 2 blocks of 512 + 288
        // when crossing to the next trace.
        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.Success, outcome.ResponseDisposition);

        // The bounded block planner should use a small bounded
        // number of transactions, not 800 individual transactions.
        Assert.Equal(plan.Blocks.Length, transactionCount);
        Assert.True(transactionCount <= 8, $"Expected at most 8 transactions, got {transactionCount}");
    }

    [Fact]
    public async Task IngestBatch_ActiveBlockThrows_PublishesRetryableWithoutSavedCounts()
    {
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: () => throw new InvalidOperationException("simulated"),
            gate: _gate);

        var request = BuildJsonRequest(TraceId, SpanIdPrefix + "01", "svc-a");
        var plan = ingester.Planner.Plan(ingester.Prepare(request));

        var outcome = await ingester.IngestPlannedAsync(plan);

        Assert.Equal(IngestResponseDisposition.RetryableFailure, outcome.ResponseDisposition);
        Assert.Equal(0, outcome.Saved);
        Assert.Equal(0, outcome.Rejected);
        Assert.Equal(0, outcome.Dropped);
        Assert.True(outcome.WriteResult.IsRetryable);

        using var connection = _db.OpenReadOnlyConnection();
        AssertNoRows(connection, OtelDb.SpansTable);
        AssertNoRows(connection, OtelDb.TracesTable);
    }

    [Fact]
    public async Task IngestBatch_CancelledBeforeLeaseAcquire_NoSavedCounts()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime: null,
            protection: null,
            transactionStarted: null,
            gate: _gate);

        var request = BuildJsonRequest(TraceId, SpanIdPrefix + "01", "cancelled");
        var plan = ingester.Planner.Plan(ingester.Prepare(request));

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await ingester.IngestPlannedAsync(plan, cts.Token));
    }

    [Fact]
    public async Task IngestBatch_RuntimeObservability_RecordsSavedAndRejectedAndDroppedSeparately()
    {
        var runtime = new RuntimeObservability(
            true,
            new RuntimeEpoch(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        // Stage 1: an accepted request saves 2 spans.
        var ingester = new TraceIngester(
            _db,
            NullLogger<TraceIngester>.Instance,
            runtime,
            protection: null,
            transactionStarted: null,
            gate: _gate);
        var accepted = new PreparedIngestBatch(
            new[]
            {
                BuildPreparedSpan(TraceId, SpanIdPrefix + "01", "svc-a"),
                BuildPreparedSpan(TraceId, SpanIdPrefix + "02", "svc-a"),
            }.ToImmutableArray(),
            0, 0, 0);
        var acceptedPlan = ingester.Planner.Plan(accepted);
        var acceptedOutcome = await ingester.IngestPlannedAsync(acceptedPlan);
        Assert.Equal(2, acceptedOutcome.Saved);

        // Stage 2: a request with one rejected span and one
        // oversized span combines non-retryable classifications.
        var oversize = BuildOversizedPreparedSpan(TraceId, SpanIdPrefix + "03", "svc-a");
        var partialBatch = new PreparedIngestBatch(
            new[] { BuildPreparedSpan(TraceId, SpanIdPrefix + "04", "svc-a"), oversize }.ToImmutableArray(),
            1, 0, 0);
        var partialPlan = ingester.Planner.Plan(partialBatch);
        var partialOutcome = await ingester.IngestPlannedAsync(partialPlan);
        Assert.Equal(IngestResponseDisposition.PartialSuccess, partialOutcome.ResponseDisposition);
        Assert.Equal(1, partialOutcome.Saved);
        Assert.Equal(1, partialOutcome.Rejected);
        Assert.Equal(1, partialOutcome.Dropped);
        Assert.Equal(2, partialOutcome.Received);

        // received = 2 (stage 1) + 2 (stage 2: 1 saved + 1 rejected) = 4
        // saved = 2 (stage 1) + 1 (stage 2) = 3
        // rejected = 0 (stage 1) + 1 (stage 2) = 1
        // dropped = 0 (stage 1) + 1 (stage 2) = 1
        var snapshot = runtime.GetSnapshot();
        Assert.Equal(4, snapshot.ReceivedSpans);
        Assert.Equal(3, snapshot.SavedSpans);
        Assert.Equal(1, snapshot.RejectedSpans);
        Assert.Equal(1, snapshot.DroppedSpans);
    }

    private static OtlpTraceRequest BuildJsonRequest(string traceId, string spanId, string serviceName)
    {
        var payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"PLACEHOLDER_SERVICE"}}]},
                "scopeSpans": [{
                  "spans": [{
                    "traceId":"PLACEHOLDER_TRACE","spanId":"PLACEHOLDER_SPAN","name":"GET /x",
                    "startTimeUnixNano":"PLACEHOLDER_START","endTimeUnixNano":"PLACEHOLDER_END"
                  }]
                }]
              }]
            }
            """;
        payload = payload
            .Replace("PLACEHOLDER_SERVICE", serviceName)
            .Replace("PLACEHOLDER_TRACE", traceId)
            .Replace("PLACEHOLDER_SPAN", spanId)
            .Replace("PLACEHOLDER_START", StartNanos)
            .Replace("PLACEHOLDER_END", EndNanos);
        return JsonSerializer.Deserialize<OtlpTraceRequest>(payload, OtlpJsonSerializer.Options())!;
    }

    private static OtlpTraceRequest BuildCorrectedJsonRequest(string traceId, string spanId, string serviceName)
    {
        var payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"PLACEHOLDER_SERVICE"}}]},
                "scopeSpans": [{
                  "spans": [{
                    "traceId":"PLACEHOLDER_TRACE","spanId":"PLACEHOLDER_SPAN","name":"CORRECTED",
                    "startTimeUnixNano":"PLACEHOLDER_START","endTimeUnixNano":"PLACEHOLDER_END"
                  }]
                }]
              }]
            }
            """;
        payload = payload
            .Replace("PLACEHOLDER_SERVICE", serviceName)
            .Replace("PLACEHOLDER_TRACE", traceId)
            .Replace("PLACEHOLDER_SPAN", spanId)
            .Replace("PLACEHOLDER_START", StartNanos)
            .Replace("PLACEHOLDER_END", EndNanos);
        return JsonSerializer.Deserialize<OtlpTraceRequest>(payload, OtlpJsonSerializer.Options())!;
    }

    private static PreparedIngestSpan BuildPreparedSpan(string traceId, string spanId, string serviceName)
    {
        var span = new NormalizedIngestSpan(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: null,
            Name: "test",
            Kind: 1,
            StartTime: "2026-01-01T00:00:00.0000000Z",
            EndTime: "2026-01-01T00:00:01.0000000Z",
            StatusCode: 1,
            StatusMessage: null,
            AttributesJson: null);
        return new PreparedIngestSpan(span, serviceName, null);
    }

    private static PreparedIngestSpan BuildOversizedPreparedSpan(string traceId, string spanId, string serviceName)
    {
        var attributeValue = new string('z', OtlpWriteBlockPlanner.MaxBlockBytes + 1);
        var attributesJson = "[{\"key\":\"payload\",\"value\":\"" + attributeValue + "\"}]";
        var span = new NormalizedIngestSpan(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: null,
            Name: "oversized",
            Kind: 1,
            StartTime: "2026-01-01T00:00:00.0000000Z",
            EndTime: "2026-01-01T00:00:01.0000000Z",
            StatusCode: 1,
            StatusMessage: null,
            AttributesJson: attributesJson);
        return new PreparedIngestSpan(span, serviceName, null);
    }

    private static void AssertSpanRow(Microsoft.Data.Sqlite.SqliteConnection connection, string traceId, string spanId, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.SpansNameColumn} FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $t AND {OtelDb.SpansSpanIdColumn} = $s";
        cmd.Parameters.AddWithValue("$t", traceId);
        cmd.Parameters.AddWithValue("$s", spanId);
        Assert.Equal(name, (string?)cmd.ExecuteScalar());
    }

    private static void AssertNoSpanRow(Microsoft.Data.Sqlite.SqliteConnection connection, string traceId, string spanId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $t AND {OtelDb.SpansSpanIdColumn} = $s";
        cmd.Parameters.AddWithValue("$t", traceId);
        cmd.Parameters.AddWithValue("$s", spanId);
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    private static void AssertTraceHeader(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string traceId,
        string serviceName,
        long spanCount,
        string startIso,
        string endIso)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesStartTimeColumn}, {OtelDb.TracesEndTimeColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $t";
        cmd.Parameters.AddWithValue("$t", traceId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"Expected trace header for {traceId}");
        Assert.Equal(serviceName, reader.GetString(0));
        Assert.Equal(startIso, reader.GetString(1));
        Assert.Equal(endIso, reader.GetString(2));
        Assert.Equal(spanCount, reader.GetInt64(3));
    }

    private static void AssertNoRows(Microsoft.Data.Sqlite.SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    private sealed class RejectAllProtectionDecision : IIngestProtectionDecision
    {
        public IngestProtectionDecision Decide(PreparedIngestSpan span) =>
            IngestProtectionDecision.Reject();
    }
}
