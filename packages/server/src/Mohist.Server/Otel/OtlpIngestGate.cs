namespace Mohist.Server.Otel;

/// <summary>
/// Singleton OTLP ingestion gate. Holds four non-waiting request leases
/// and one writer lease shared by the OTLP trace route. Admissions
/// beyond the configured limit return immediately without reading the
/// request body, and the gate retains no queue or background worker.
/// </summary>
public sealed class OtlpIngestGate : IOtlpIngestGate
{
    public const int RequestLeaseLimit = 4;
    public const int TemporaryAdmissionRetryAfterSeconds = 1;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writerLease = new(1, 1);
    private int _requestLeasesInUse;

    public OtlpIngestGate()
    {
    }

    public int RequestLeasesInUse
    {
        get
        {
            lock (_gate)
                return _requestLeasesInUse;
        }
    }

    public OtlpAdmissionDecision TryAcquireRequestLease()
    {
        lock (_gate)
        {
            if (_requestLeasesInUse >= RequestLeaseLimit)
                return OtlpAdmissionDecision.Rejected(TemporaryAdmissionRetryAfterSeconds);
            _requestLeasesInUse++;
            return OtlpAdmissionDecision.Acquired();
        }
    }

    public void ReleaseRequestLease()
    {
        lock (_gate)
        {
            if (_requestLeasesInUse == 0)
                throw new InvalidOperationException("No request lease is held.");
            _requestLeasesInUse--;
        }
    }

    public async Task<OtlpWriterLease> AcquireWriterLeaseAsync(CancellationToken ct)
    {
        await _writerLease.WaitAsync(ct).ConfigureAwait(false);
        return new OtlpWriterLease(this);
    }

    internal void ReleaseWriterLease()
    {
        try
        {
            _writerLease.Release();
        }
        catch (SemaphoreFullException ex)
        {
            throw new InvalidOperationException("No writer lease is held.", ex);
        }
    }

}

public interface IOtlpIngestGate
{
    OtlpAdmissionDecision TryAcquireRequestLease();

    void ReleaseRequestLease();

    Task<OtlpWriterLease> AcquireWriterLeaseAsync(CancellationToken ct);
}

public readonly record struct OtlpAdmissionDecision(bool Admitted, int RetryAfterSeconds)
{
    public static OtlpAdmissionDecision Acquired() => new(true, 0);

    public static OtlpAdmissionDecision Rejected(int retryAfterSeconds) => new(false, retryAfterSeconds);
}

public readonly struct OtlpWriterLease : IDisposable
{
    private readonly OtlpIngestGate? _owner;

    internal OtlpWriterLease(OtlpIngestGate owner)
    {
        _owner = owner;
    }

    public void Dispose()
    {
        _owner?.ReleaseWriterLease();
    }

    public bool IsHeld => _owner is not null;
}
