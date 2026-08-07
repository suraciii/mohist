using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Test fake for <see cref="IEventTailSource"/>. Wraps a fresh
/// <see cref="EventTailSource"/> singleton (so the production handler
/// logic — strict project isolation, compiled-expression filter,
/// non-blocking drop-on-full channel — is exercised unchanged) and
/// exposes its surface so specs can publish envelopes directly and
/// observe subscription lifecycle without driving the durable event
/// dispatcher or touching a wall clock.
/// </summary>
public sealed class InMemoryEventTailSource : IEventTailSource
{
    private readonly EventTailSource _inner;
    private Action<string>? _subscriptionOpened;

    public InMemoryEventTailSource()
    {
        _inner = new EventTailSource(NullLogger<EventTailSource>.Instance);
    }

    public EventTailSubscription Open(string projectId, EventMatchExpression? match)
    {
        var subscription = _inner.Open(projectId, match);
        _subscriptionOpened?.Invoke(projectId);
        return subscription;
    }

    public IDisposable ObserveSubscriptionOpened(Action<string> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _subscriptionOpened += observer;
        return new SubscriptionObserver(this, observer);
    }

    public void Publish(CloudEvent envelope)
        => _inner.Publish(envelope);

    public int ActiveSubscriptionCount => _inner.ActiveSubscriptionCount;

    private sealed class SubscriptionObserver(
        InMemoryEventTailSource source,
        Action<string> observer) : IDisposable
    {
        public void Dispose() => source._subscriptionOpened -= observer;
    }
}
