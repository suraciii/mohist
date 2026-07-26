using System.Collections.Immutable;

namespace Mohist.Server.Otel;

/// <summary>
/// A single OTLP write block. The block contains a contiguous slice
/// of the accepted parsed Span classification and the cumulative
/// non-retryable classification (oversized Spans dropped by the
/// planner, plus the protection-rejected count that arrived before
/// planning).
/// </summary>
/// <remarks>
/// Design D3. A block is bounded by either
/// <see cref="OtlpWriteBlockPlanner.MaxBlockBytes"/> or
/// <see cref="OtlpWriteBlockPlanner.MaxSpansPerBlock"/>, whichever
/// limit is reached first. The block is the unit of database
/// commit; the receiver never starts a transaction that contains
/// more than one block.
/// </remarks>
public sealed record OtlpWriteBlock(
    ImmutableArray<PreparedIngestSpan> Spans,
    long CumulativeRejected,
    long CumulativeDropped,
    long CumulativeOversizedDropped);

/// <summary>
/// Plan describing the bounded write blocks for one accepted OTLP
/// request. The planner is a pure function of the parsed-for-write
/// classifications and the immutable block limits; it does not
/// observe the database and never allocates output beyond the
/// planned blocks.
/// </summary>
public sealed record OtlpWritePlan(
    ImmutableArray<OtlpWriteBlock> Blocks,
    long ProtectionRejected,
    long MalformedDropped,
    long OversizedDropped)
{
    public long TotalRejected => ProtectionRejected;
    public long TotalDropped => MalformedDropped + OversizedDropped;
    public long TotalAccepted => Blocks.Sum(static b => (long)b.Spans.Length);
}

/// <summary>
/// Partitions accepted parsed-for-write classifications into
/// bounded write blocks. The planner walks the classifications in
/// arrival order, accumulating deterministic persisted-data weight
/// and Span count, and closes a block before adding a Span that
/// would exceed either limit. A single normalized Span whose weight
/// alone exceeds <see cref="MaxBlockBytes"/> becomes a non-retryable
/// dropped classification with a bounded reason; it is never
/// scheduled into a block.
/// </summary>
/// <remarks>
/// Design D3. The output is immutable; the ingester iterates the
/// blocks sequentially and releases each one after its transaction
/// commits. The planner retains no queue and no per-Span
/// intermediate state beyond a running counter.
/// </remarks>
public sealed class OtlpWriteBlockPlanner
{
    public const int MaxBlockBytes = 4 * 1024 * 1024;
    public const int MaxSpansPerBlock = 512;
    public const int OversizedSpanMaxBytes = MaxBlockBytes;

    private static readonly BoundedOversizedReason OversizedReason = new();

    public OtlpWritePlan Plan(PreparedIngestBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var blocks = ImmutableArray.CreateBuilder<OtlpWriteBlock>();
        var currentSpans = ImmutableArray.CreateBuilder<PreparedIngestSpan>(MaxSpansPerBlock);
        long currentBytes = 0;

        long cumulativeRejected = batch.ProtectionRejected;
        long cumulativeDropped = batch.MalformedDropped;
        long cumulativeOversized = 0;

        foreach (var prepared in batch.ParsedSpans)
        {
            var spanWeight = OtlpSpanWeight.Measure(prepared.Span, prepared.ResourceAttributesJson);

            if (spanWeight > MaxBlockBytes)
            {
                cumulativeOversized++;
                cumulativeDropped++;
                if (currentSpans.Count > 0)
                {
                    blocks.Add(new OtlpWriteBlock(
                        currentSpans.ToImmutable(),
                        cumulativeRejected,
                        cumulativeDropped,
                        cumulativeOversized));
                    currentSpans = ImmutableArray.CreateBuilder<PreparedIngestSpan>(MaxSpansPerBlock);
                    currentBytes = 0;
                }
                continue;
            }

            if (currentSpans.Count > 0 && (currentSpans.Count >= MaxSpansPerBlock || currentBytes + spanWeight > MaxBlockBytes))
            {
                blocks.Add(new OtlpWriteBlock(
                    currentSpans.ToImmutable(),
                    cumulativeRejected,
                    cumulativeDropped,
                    cumulativeOversized));
                currentSpans = ImmutableArray.CreateBuilder<PreparedIngestSpan>(MaxSpansPerBlock);
                currentBytes = 0;
            }

            currentSpans.Add(prepared);
            currentBytes += spanWeight;
        }

        if (currentSpans.Count > 0)
        {
            blocks.Add(new OtlpWriteBlock(
                currentSpans.ToImmutable(),
                cumulativeRejected,
                cumulativeDropped,
                cumulativeOversized));
        }

        return new OtlpWritePlan(
            blocks.ToImmutable(),
            batch.ProtectionRejected,
            batch.MalformedDropped,
            cumulativeOversized);
    }

    /// <summary>
    /// Built-in drop reason for a single oversized normalized Span.
    /// The diagnostic text is bounded to 256 characters and fits
    /// the existing drain-into-partial-success envelope.
    /// </summary>
    public static string BuildOversizedSpanReason(long spanWeight) =>
        OversizedReason.Reason(spanWeight);

    private sealed class BoundedOversizedReason
    {
        private const string Prefix = "Oversized span dropped: persisted weight ";
        private const string Suffix = " bytes exceeds the per-block limit; it cannot be written as a single transaction.";

        public string Reason(long spanWeight)
        {
            var raw = Prefix + spanWeight.ToString(System.Globalization.CultureInfo.InvariantCulture) + Suffix;
            return raw.Length <= 256 ? raw : raw[..256];
        }
    }
}
