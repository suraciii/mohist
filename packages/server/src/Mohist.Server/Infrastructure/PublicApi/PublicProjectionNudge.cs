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
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    /// <summary>Awaitable signal used by the hosted projector's wait loop.</summary>
    internal ValueTask<bool> WaitAsync(CancellationToken ct) => _signals.Reader.ReadAsync(ct);

    /// <summary>
    /// Coalescing write: repeated nudges before the projector wakes
    /// collapse into one pending signal.
    /// </summary>
    public void Nudge()
    {
        _signals.Writer.TryWrite(true);
    }
}
