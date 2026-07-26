using System.Text;

namespace Mohist.Server.Otel;

/// <summary>
/// Deterministic UTF-8 byte weight of the persisted columns for a
/// single normalized Span. The weight deliberately measures the
/// stored representation (the bytes SQLite will occupy) rather than
/// the wire payload, so the block planner can bound a transaction
/// before it begins and the
/// <see cref="OtlpWriteBlockPlanner.MaxBlockBytes"/> limit caps
/// memory and write amplification.
/// </summary>
/// <remarks>
/// Design D3 / D4. The numbers are the column payload bytes plus
/// SQLite's per-row primary-key overhead; the additive
/// trace-time indexes assume the weight is dominated by the
/// attribute and resource JSON columns. The result is a stable upper
/// bound — overestimating under-counts fill, underestimating risks
/// oversize transactions, so the deliberately conservative estimate
/// is preferred.
/// </remarks>
internal static class OtlpSpanWeight
{
    /// <summary>Fixed per-Span overhead for the row header and primary key.</summary>
    internal const int FixedOverheadBytes = 96;

    /// <summary>UTF-8 byte weight of the persisted string columns.</summary>
    public static long Measure(NormalizedIngestSpan span, string? resourceAttributesJson)
    {
        var utf8 = Encoding.UTF8;
        long total = FixedOverheadBytes;
        total += utf8.GetByteCount(span.TraceId);
        total += utf8.GetByteCount(span.SpanId);
        if (span.ParentSpanId is not null)
            total += utf8.GetByteCount(span.ParentSpanId);
        total += utf8.GetByteCount(span.Name);
        total += utf8.GetByteCount(span.StartTime);
        total += utf8.GetByteCount(span.EndTime);
        if (span.StatusMessage is not null)
            total += utf8.GetByteCount(span.StatusMessage);
        if (span.AttributesJson is not null)
            total += utf8.GetByteCount(span.AttributesJson);
        if (resourceAttributesJson is not null)
            total += utf8.GetByteCount(resourceAttributesJson);
        return total;
    }
}
