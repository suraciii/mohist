namespace Mohist.Server.Otel;

/// <summary>
/// Ingest protection decision backed by the storage-budget
/// admission guard. When <see cref="OtelStorageGuard.AdmissionClosed"/>
/// is true, every Span is rejected so the existing
/// <see cref="TraceIngester"/> rejection accounting fires and OTLP
/// <c>partial_success</c> reports the rejected Span count without
/// the sender retrying. The decision is a volatile read on the
/// guard, so the write path completes without blocking.
/// </summary>
public sealed class BudgetAwareIngestProtectionDecision : IIngestProtectionDecision
{
    private readonly OtelStorageGuard _guard;

    public BudgetAwareIngestProtectionDecision(OtelStorageGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guard = guard;
    }

    public IngestProtectionDecision Decide(PreparedIngestSpan span) =>
        _guard.AdmissionClosed
            ? IngestProtectionDecision.Reject()
            : IngestProtectionDecision.Accept();
}
