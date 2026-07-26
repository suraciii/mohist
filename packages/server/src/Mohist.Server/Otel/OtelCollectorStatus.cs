namespace Mohist.Server.Otel;

/// <summary>
/// Process-wide record of whether the OTLP ingestion port is currently
/// bound and accepting traffic. Updated by the host startup path
/// when Kestrel reports the OTLP port's status, and read by
/// <c>/otel/api/status</c> so callers can tell whether the
/// collector is online.
/// </summary>
public sealed class OtelCollectorStatus
{
    private readonly object _gate = new();
    private bool _portBound;

    /// <summary>Whether the OTLP ingestion port is bound.</summary>
    public bool IsPortBound
    {
        get
        {
            lock (_gate)
            {
                return _portBound;
            }
        }
    }

    /// <summary>
    /// Snapshot of the current collector state. Use this from request
    /// handlers to read the bound flag without holding the lock across
    /// I/O.
    /// </summary>
    public OtelCollectorState Current => new(IsPortBound);

    /// <summary>
    /// Records the current bound state. Safe to call from any thread.
    /// </summary>
    public void SetPortBound(bool portBound)
    {
        lock (_gate)
        {
            _portBound = portBound;
        }
    }
}

/// <summary>Immutable snapshot of <see cref="OtelCollectorStatus"/>.</summary>
public readonly record struct OtelCollectorState(bool IsPortBound);
