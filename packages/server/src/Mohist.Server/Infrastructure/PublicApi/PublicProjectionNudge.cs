using System.Threading.Channels;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// The in-process latency nudge for the public execution projector.
/// Canonical write paths signal it best-effort right after their own
/// commit; the hosted projector treats a nudge as "poll now" and falls
/// back to its timer sweep when nudges are lost, because projection
/// correctness is checkpoint-driven and never depends on the nudge.
/// </summary>
public interface IPublicProjectionNudge
{
    /// <summary>Signals the projector that new canonical facts may be durable. Never throws, never blocks.</summary>
    void Nudge();
}

public sealed class PublicProjectionNudge : IPublicProjectionNudge
{
    private readonly Channel<long> _signals = Channel.CreateBounded<long>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly object _gate = new();
    private readonly List<DrainWaiter> _waiters = [];
    private long _requestedGeneration;

    /// <summary>Awaitable signal used by the hosted projector's wait loop.</summary>
    internal ValueTask<long> WaitAsync(CancellationToken ct) => _signals.Reader.ReadAsync(ct);

    internal long LatestGeneration => Volatile.Read(ref _requestedGeneration);

    /// <summary>
    /// Coalescing write: repeated nudges before the projector wakes
    /// collapse into one pending signal.
    /// </summary>
    public void Nudge()
    {
        Signal();
    }

    internal async Task NudgeAndWaitAsync(CancellationToken ct = default)
    {
        var generation = Interlocked.Increment(ref _requestedGeneration);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add(new DrainWaiter(generation, completion));
        }

        _signals.Writer.TryWrite(generation);
        await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    internal void Complete(long generation) => Settle(generation, error: null);

    internal void Fail(long generation, Exception error) => Settle(generation, error);

    private void Signal()
    {
        var generation = Interlocked.Increment(ref _requestedGeneration);
        _signals.Writer.TryWrite(generation);
    }

    private void Settle(long generation, Exception? error)
    {
        List<DrainWaiter> settled;
        lock (_gate)
        {
            settled = _waiters.Where(waiter => waiter.Generation <= generation).ToList();
            _waiters.RemoveAll(waiter => waiter.Generation <= generation);
        }

        foreach (var waiter in settled)
        {
            if (error is null)
                waiter.Completion.TrySetResult();
            else
                waiter.Completion.TrySetException(error);
        }
    }

    private sealed record DrainWaiter(long Generation, TaskCompletionSource Completion);
}
