using Microsoft.Extensions.Options;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// In-process wake signal between event producers and dispatch workers.
/// A producer writes one signal after its transaction commits; an idle
/// worker consumes it and runs a discovery pass. Signals are best-effort:
/// a lost signal costs at most one <see cref="EventDispatcherOptions.SlowPollInterval"/>
/// of latency, never correctness.
/// </summary>
public sealed class EventDispatchSignal
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake;
    private readonly int _capacity;

    public EventDispatchSignal(int capacity = 4)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _wake = new SemaphoreSlim(0, capacity);
    }

    /// <summary>Never blocks, never throws; excess signals beyond capacity
    /// collapse — those workers are already awake or about to poll.</summary>
    public void Wake()
    {
        lock (_gate)
        {
            if (_wake.CurrentCount < _capacity)
                _wake.Release();
        }
    }

    /// <summary>Returns true when a signal arrived before the timeout.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _wake.WaitAsync(timeout, ct);
}
