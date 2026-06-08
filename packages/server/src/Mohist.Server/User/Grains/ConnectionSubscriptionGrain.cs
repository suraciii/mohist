namespace Mohist.Server.User.Grains;

/// <summary>
/// In-memory implementation of <see cref="IConnectionSubscriptionGrain"/>.
/// State is held in an instance field; Orleans single-threaded activation
/// guarantees that all <c>Subscribe</c> / <c>Unsubscribe</c> /
/// <c>ShouldNotify</c> calls are serialised, so the
/// <see cref="HashSet{T}"/> does not need an internal lock.
///
/// <para>
/// <b>Durability</b>. The current implementation is purely
/// in-memory. If the silo restarts, the subscription set is lost.
/// On reconnect, the SignalR hub will replay the client's last
/// <c>SetSubscriptionsAsync</c> (the Web UI calls it on tab
/// open), so a "lost on restart" window is bounded by the
/// client's reconnect interval. If cross-restart durability ever
/// becomes a requirement, the grain can be backed by an
/// <c>IStateStore&lt;HashSet&lt;string&gt;&gt;</c> with the same
/// shape used by <c>IssueCounterGrain</c>.
/// </para>
/// </summary>
public class ConnectionSubscriptionGrain : Grain, IConnectionSubscriptionGrain
{
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);

    public Task SetSubscriptionsAsync(IReadOnlyCollection<string> eventTypes)
    {
        _subscriptions.Clear();
        if (eventTypes is null)
        {
            return Task.CompletedTask;
        }
        foreach (var t in eventTypes)
        {
            if (!string.IsNullOrEmpty(t))
            {
                _subscriptions.Add(t);
            }
        }
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string eventType)
    {
        if (!string.IsNullOrEmpty(eventType))
        {
            _subscriptions.Add(eventType);
        }
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string eventType)
    {
        if (!string.IsNullOrEmpty(eventType))
        {
            _subscriptions.Remove(eventType);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetSubscriptionsAsync()
    {
        // Return a copy. The caller (the SignalR hub) holds the
        // returned reference past the grain call; a live view of
        // the grain's internal set would race with later Subscribe
        // calls.
        var snapshot = new HashSet<string>(_subscriptions, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlySet<string>>(snapshot);
    }
}
