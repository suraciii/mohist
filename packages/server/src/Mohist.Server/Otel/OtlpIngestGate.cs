namespace Mohist.Server.Otel;

/// <summary>
/// Singleton OTLP ingestion gate. Holds four non-waiting request leases
/// and one writer lease shared by the OTLP trace route. Admissions
/// beyond the configured limit return immediately without reading the
/// request body, and the gate retains no queue or background worker.
/// </summary>
/// <remarks>
/// Design D1. The gate exposes a structurally identical
/// <see cref="IOtlpIngestGate"/> surface so a test can supply a fake
/// with deterministic wait/release control; the production
/// implementation instantiates the singleton that
/// <c>MohistServiceRegistration</c> registers.
/// </remarks>
public sealed class OtlpIngestGate : IOtlpIngestGate, IOtlpIngestGateTestSeam
{
    public const int RequestLeaseLimit = 4;
    public const int TemporaryAdmissionRetryAfterSeconds = 1;

    private readonly object _gate = new();
    private int _requestLeasesInUse;
    private bool _writerLeaseInUse;
    private TaskCompletionSource<bool>? _writerSignal;
    private TaskCompletionSource<bool>? _nextRequestSignal;

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
        TaskCompletionSource<bool>? signalToWake = null;
        lock (_gate)
        {
            if (_requestLeasesInUse == 0)
                throw new InvalidOperationException("No request lease is held.");
            _requestLeasesInUse--;
            if (_requestLeasesInUse == RequestLeaseLimit - 1)
            {
                signalToWake = _nextRequestSignal;
                _nextRequestSignal = null;
            }
        }
        signalToWake?.TrySetResult(true);
    }

    public Task<OtlpWriterLease> AcquireWriterLeaseAsync(CancellationToken ct)
    {
        OtlpIngestGate owner = this;
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            if (!_writerLeaseInUse)
            {
                _writerLeaseInUse = true;
                return Task.FromResult(new OtlpWriterLease(owner));
            }
            _writerSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = _writerSignal;
        }
        return AwaitWriterLeaseAsync(owner, waiter!, ct);
    }

    private static async Task<OtlpWriterLease> AwaitWriterLeaseAsync(
        OtlpIngestGate owner,
        TaskCompletionSource<bool> waiter,
        CancellationToken ct)
    {
        if (waiter.Task.IsCompleted)
            return new OtlpWriterLease(owner);

        using var registration = ct.Register(static state =>
        {
            var source = (TaskCompletionSource<bool>)state!;
            source.TrySetCanceled();
        }, waiter);

        await waiter.Task.ConfigureAwait(false);
        return new OtlpWriterLease(owner);
    }

    internal void ReleaseWriterLease()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_gate)
        {
            if (!_writerLeaseInUse)
                throw new InvalidOperationException("No writer lease is held.");
            _writerLeaseInUse = false;
            signal = _writerSignal;
            _writerSignal = null;
        }
        signal?.TrySetResult(true);
    }

    /// <summary>
    /// Test-only seam that blocks the next request-lease acquisition
    /// until <see cref="ReleaseNextRequestSignal"/> is called. Used by
    /// the integration specs to drive the four-slot exhaustion path
    /// without scheduler or wall-clock waits.
    /// </summary>
    internal void BlockNextRequestLease()
    {
        lock (_gate)
            _nextRequestSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Resolves the pending request-lease wait signal created by
    /// <see cref="BlockNextRequestLease"/>. Throws if no signal is
    /// pending.
    /// </summary>
    internal void ReleaseNextRequestSignal()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_gate)
        {
            signal = _nextRequestSignal;
            _nextRequestSignal = null;
        }
        signal?.TrySetResult(true);
    }

    bool IOtlpIngestGateTestSeam.BlockNextRequestLease()
    {
        lock (_gate)
        {
            if (_nextRequestSignal is not null)
                return false;
            _nextRequestSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    void IOtlpIngestGateTestSeam.ReleaseNextRequestSignal() => ReleaseNextRequestSignal();
}

public interface IOtlpIngestGateTestSeam
{
    bool BlockNextRequestLease();

    void ReleaseNextRequestSignal();
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
