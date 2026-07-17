using System.Threading.Channels;
using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Consumer-facing seam for the project-scoped live event tail endpoint
/// (<c>GET /api/projects/{projectRef}/events/tail</c>). The endpoint opens a
/// transient <see cref="EventTailSubscription"/> against the source, reads
/// matching envelopes for the lifetime of the request, and disposes the
/// subscription on cancellation or disconnect.
/// </summary>
/// <remarks>
/// <para>
/// The interface is registered as the singleton <c>EventTailSource</c>
/// in production. Server specs swap it for an in-memory fake so they can
/// drive the endpoint without the durable event dispatcher and without
/// touching a wall clock.
/// </para>
/// <para>
/// Subscriptions are strictly project-scoped. <see cref="Publish"/> drops
/// every envelope whose <c>projectid</c> extension does not equal the
/// subscription's resolved project, or that carries no
/// <c>projectid</c> extension at all (strict isolation — no fallback to
/// type-only matching).
/// </para>
/// <para>
/// The compiled <see cref="EventMatchExpression"/> is evaluated
/// envelope-side; payloads are never consulted.
/// </para>
/// </remarks>
public interface IEventTailSource
{
    /// <summary>
    /// Opens a transient subscription scoped to <paramref name="projectId"/>
    /// with the optional compiled <paramref name="match"/> expression. The
    /// returned <see cref="EventTailSubscription"/> is owned by the caller
    /// and must be disposed (or its cancellation token cancelled) to
    /// release the channel and unsubscribe.
    /// </summary>
    EventTailSubscription Open(string projectId, EventMatchExpression? match);

    /// <summary>
    /// Pushes an envelope through the source. Used by the bus-side
    /// <c>EventTailSource</c> handler in production and by server specs to
    /// drive the endpoint without touching the durable dispatcher.
    /// Implementations apply strict project isolation and the
    /// per-subscription match filter before <c>TryWrite</c>'ing to the tail
    /// channel.
    /// </summary>
    void Publish(CloudEvent envelope);

    /// <summary>
    /// Number of currently-open subscriptions. Test-only observability
    /// seam (verifies release-on-disconnect without inspecting private
    /// state).
    /// </summary>
    int ActiveSubscriptionCount { get; }
}

/// <summary>
/// A single project-scoped tail subscription. The consumer reads
/// envelopes from <see cref="Reader"/> until the request ends or the
/// matched scope is exhausted. Disposal releases the channel and invokes
/// the release callback so the owning source can remove the subscription
/// from its fan-out table.
/// </summary>
public sealed class EventTailSubscription : IAsyncDisposable
{
    private readonly Channel<CloudEvent> _channel;
    private readonly Action _release;
    private int _disposed;

    internal EventTailSubscription(
        Channel<CloudEvent> channel,
        Action release)
    {
        _channel = channel;
        _release = release;
    }

    /// <summary>
    /// The channel reader for this subscription. The endpoint awaits new
    /// envelopes and writes one compact JSON object per event to the
    /// NDJSON response body.
    /// </summary>
    public ChannelReader<CloudEvent> Reader => _channel.Reader;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _channel.Writer.TryComplete();
        _release();
        await Task.CompletedTask;
    }
}