using System.Collections.Immutable;
using Mohist.Server.Otel.OtlpJson;

namespace Mohist.Server.Otel;

public interface IIngestProtectionDecision
{
    IngestProtectionDecision Decide(PreparedIngestSpan span);
}

public sealed record IngestProtectionDecision(bool Accepted)
{
    public static IngestProtectionDecision Accept() => new(true);
    public static IngestProtectionDecision Reject() => new(false);
}

public sealed class AcceptAllIngestProtectionDecision : IIngestProtectionDecision
{
    public IngestProtectionDecision Decide(PreparedIngestSpan span) =>
        IngestProtectionDecision.Accept();
}

public sealed record PreparedIngestSpan(
    NormalizedIngestSpan Span,
    string ServiceName,
    string? ResourceAttributesJson);

public sealed record PreparedIngestBatch(
    ImmutableArray<PreparedIngestSpan> ParsedSpans,
    long ProtectionRejected,
    long MalformedDropped,
    long OtherDropped)
{
    public ClassifiedBatchTotals Classification => new(
        ParsedSpans.Length,
        ProtectionRejected,
        MalformedDropped,
        OtherDropped);
}

public readonly record struct NormalizedIngestSpan(
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
