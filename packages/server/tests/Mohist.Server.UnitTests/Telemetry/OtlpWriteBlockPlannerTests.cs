using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Mohist.Server.Otel;
using Mohist.Server.Otel.OtlpJson;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class OtlpWriteBlockPlannerTests
{
    [Fact]
    public void Plan_EmptyBatch_ReturnsEmptyPlan()
    {
        var planner = new OtlpWriteBlockPlanner();
        var batch = new PreparedIngestBatch(ImmutableArray<PreparedIngestSpan>.Empty, 0, 0, 0);

        var plan = planner.Plan(batch);

        Assert.Empty(plan.Blocks);
        Assert.Equal(0, plan.TotalAccepted);
        Assert.Equal(0, plan.OversizedDropped);
    }

    [Fact]
    public void Plan_BelowLimit_FitsSingleBlock()
    {
        var planner = new OtlpWriteBlockPlanner();
        var batch = BuildBatch(spanCount: 3, spanNameSize: 64);

        var plan = planner.Plan(batch);

        var block = Assert.Single(plan.Blocks);
        Assert.Equal(3, block.Spans.Length);
        Assert.Equal(0, plan.OversizedDropped);
    }

    [Fact]
    public void Plan_HitsSpanCountLimit_SplitsAt512()
    {
        var planner = new OtlpWriteBlockPlanner();
        var batch = BuildBatch(spanCount: OtlpWriteBlockPlanner.MaxSpansPerBlock + 25, spanNameSize: 32);

        var plan = planner.Plan(batch);

        Assert.Equal(2, plan.Blocks.Length);
        Assert.Equal(OtlpWriteBlockPlanner.MaxSpansPerBlock, plan.Blocks[0].Spans.Length);
        Assert.Equal(25, plan.Blocks[1].Spans.Length);
    }

    [Fact]
    public void Plan_HitsByteLimit_SplitsBeforeBlockWouldExceed()
    {
        var planner = new OtlpWriteBlockPlanner();
        // Each Spans's stored weight is large enough that the byte
        // limit is reached before the Span-count limit for any
        // block. 4 MiB / 8192 bytes ≈ 512 spans, but two blocks
        // must appear before the count limit is hit.
        var perSpanPayload = new string('z', 8192);
        var batch = BuildBatchWithPayload(spanCount: 700, attributePayload: perSpanPayload);

        var plan = planner.Plan(batch);

        Assert.Equal(0, plan.OversizedDropped);
        Assert.All(plan.Blocks, b =>
        {
            long weight = 0;
            foreach (var s in b.Spans)
                weight += OtlpSpanWeight.Measure(s.Span, s.ResourceAttributesJson);
            Assert.True(weight <= OtlpWriteBlockPlanner.MaxBlockBytes,
                $"Block weight {weight} exceeds limit {OtlpWriteBlockPlanner.MaxBlockBytes}.");
        });
        Assert.True(plan.Blocks.Length >= 2);
    }

    [Fact]
    public void Plan_SingleSpanOverByteLimit_ClassifiedAsOversizedDroppedNotScheduled()
    {
        var planner = new OtlpWriteBlockPlanner();
        var oversizedAttribute = new string('z', OtlpWriteBlockPlanner.MaxBlockBytes + 1);
        var batch = BuildBatchWithPayload(spanCount: 1, attributePayload: oversizedAttribute);

        var plan = planner.Plan(batch);

        Assert.Empty(plan.Blocks);
        Assert.Equal(1, plan.OversizedDropped);
        Assert.Equal(1, plan.TotalDropped);
    }

    [Fact]
    public void Plan_OversizedSpan_ClosesPriorBlockThenDrops()
    {
        var planner = new OtlpWriteBlockPlanner();
        // Build 3 small spans with no payload, then make the second
        // one oversized. The first span fits in block 1, the
        // oversized span closes block 1 and is dropped, and the
        // third span starts block 2.
        var batch = BuildBatch(spanCount: 3, spanNameSize: 16);
        var oversizedAttribute = new string('z', OtlpWriteBlockPlanner.MaxBlockBytes + 1);
        var spans = batch.ParsedSpans.ToBuilder();
        var oversized = new PreparedIngestSpan(
            spans[1].Span,
            spans[1].ServiceName,
            SerializeAttribute(oversizedAttribute));
        spans[1] = oversized;
        var mixed = new PreparedIngestBatch(spans.ToImmutable(), batch.ProtectionRejected, batch.MalformedDropped, 0);

        var plan = planner.Plan(mixed);

        Assert.Equal(2, plan.TotalAccepted);
        Assert.Equal(1, plan.OversizedDropped);
        Assert.Equal(2, plan.Blocks.Length);
        Assert.Single(plan.Blocks[0].Spans);
        Assert.Single(plan.Blocks[1].Spans);
    }

    [Fact]
    public void Plan_ProtectionRejectedCountCarriesThrough()
    {
        var planner = new OtlpWriteBlockPlanner();
        var batch = new PreparedIngestBatch(ImmutableArray<PreparedIngestSpan>.Empty, 7, 2, 0);

        var plan = planner.Plan(batch);

        Assert.Empty(plan.Blocks);
        Assert.Equal(7, plan.ProtectionRejected);
        Assert.Equal(2, plan.MalformedDropped);
        Assert.Equal(0, plan.OversizedDropped);
    }

    [Fact]
    public void Plan_ArrivalOrderPreserved()
    {
        var planner = new OtlpWriteBlockPlanner();
        var batch = BuildBatch(spanCount: 5, spanNameSize: 16);

        var plan = planner.Plan(batch);

        var ordered = plan.Blocks.SelectMany(b => b.Spans).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var expectedPrefix = $"span-{i:D4}";
            Assert.StartsWith(expectedPrefix, ordered[i].Span.Name);
        }
    }

    [Fact]
    public void BuildOversizedSpanReason_FitsBoundedLength()
    {
        var reason = OtlpWriteBlockPlanner.BuildOversizedSpanReason(8 * 1024 * 1024);
        Assert.True(reason.Length <= 256);
        Assert.Contains("Oversized span dropped", reason);
    }

    private static PreparedIngestBatch BuildBatch(int spanCount, int spanNameSize)
    {
        var list = new List<PreparedIngestSpan>(spanCount);
        for (var i = 0; i < spanCount; i++)
        {
            var name = $"span-{i:D4}".PadRight(spanNameSize, 'x');
            var span = new NormalizedIngestSpan(
                TraceId: $"0000000000000000000000000000000{i:X1}",
                SpanId: $"000000000000{i:X4}",
                ParentSpanId: null,
                Name: name,
                Kind: 1,
                StartTime: "2026-01-01T00:00:00.0000000Z",
                EndTime: "2026-01-01T00:00:01.0000000Z",
                StatusCode: 1,
                StatusMessage: null,
                AttributesJson: null);
            list.Add(new PreparedIngestSpan(span, "svc", null));
        }
        return new PreparedIngestBatch(list.ToImmutableArray(), 0, 0, 0);
    }

    private static PreparedIngestBatch BuildBatchWithPayload(int spanCount, string attributePayload)
    {
        var list = new List<PreparedIngestSpan>(spanCount);
        var attrs = SerializeAttribute(attributePayload);
        for (var i = 0; i < spanCount; i++)
        {
            var span = new NormalizedIngestSpan(
                TraceId: $"0000000000000000000000000000000{i:X1}",
                SpanId: $"000000000000{i:X4}",
                ParentSpanId: null,
                Name: $"span-{i:D4}",
                Kind: 1,
                StartTime: "2026-01-01T00:00:00.0000000Z",
                EndTime: "2026-01-01T00:00:01.0000000Z",
                StatusCode: 1,
                StatusMessage: null,
                AttributesJson: attrs);
            list.Add(new PreparedIngestSpan(span, "svc", null));
        }
        return new PreparedIngestBatch(list.ToImmutableArray(), 0, 0, 0);
    }

    private static string SerializeAttribute(string payload)
    {
        var kv = new KeyValue
        {
            Key = "payload",
            Value = new AnyValue { Kind = AnyValueKind.String, StringValue = payload },
        };
        var projection = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["key"] = kv.Key,
                ["value"] = payload,
            },
        };
        return JsonSerializer.Serialize(projection, OtlpJsonSerializer.Options());
    }
}
