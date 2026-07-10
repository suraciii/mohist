using System.Text.Json;

namespace Mohist.Server.Infrastructure.Events;

public sealed class InMemoryEventBus : IEventPublisher
{
    private readonly IEventStore _eventStore;
    private readonly TimeProvider _time;
    private readonly ILogger<InMemoryEventBus> _log;

    public InMemoryEventBus(IEventStore eventStore, TimeProvider time, ILogger<InMemoryEventBus> log)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log;
        _subscriptions = [];
    }

    public InMemoryEventBus(
        IEnumerable<Subscription> subscriptions,
        IEventStore eventStore,
        TimeProvider time,
        ILogger<InMemoryEventBus> log)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subscriptions = subscriptions.ToList();
        _log = log;

        foreach (var sub in _subscriptions)
            ValidateType(sub.Type);

        log.LogInformation(
            "Event bus ready: {Count} subscriptions", _subscriptions.Count);
    }

    private readonly List<Subscription> _subscriptions;

    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        return _eventStore.AppendAsync(envelope, ct);
    }

    /// <summary>
    /// Adds a subscription after construction. Used by test fixtures that
    /// build the bus empty and then wire the same handlers the production
    /// pipeline registers via <c>AddCloudEventHandlersFromAssembly</c>.
    /// Subscriptions are retained for the future dispatcher but are not
    /// invoked by <see cref="PublishAsync"/>; the publish path is write-only
    /// and delegates to <see cref="IEventStore.AppendAsync(CloudEvent, CancellationToken)"/>.
    /// </summary>
    public void AddSubscription(Subscription subscription)
    {
        ValidateType(subscription.Type);
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }
    }

    public async Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default)
    {
        var dataJson = JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions);
        var extDict = extensions is null
            ? null
            : new Dictionary<string, string>(extensions, StringComparer.Ordinal);
        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.RelativeOrAbsolute),
            type: type,
            time: _time.GetUtcNow(),
            data: dataJson,
            subject: subject,
            extensions: extDict);

        await _eventStore.AppendAsync(evt, ct).ConfigureAwait(false);
    }

    private static void ValidateType(string type)
    {
        if (string.IsNullOrEmpty(type))
            throw new ArgumentException("Empty type", nameof(type));
        foreach (var alternative in type.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (alternative == "*") continue;
            if (alternative.EndsWith(".*", StringComparison.Ordinal))
            {
                if (alternative.IndexOf('*') != alternative.Length - 1)
                    throw new ArgumentException(
                        $"Invalid subscription type '{type}': wildcards are only allowed as '.*' suffix",
                        nameof(type));
            }
            else if (alternative.Contains('*'))
            {
                throw new ArgumentException(
                    $"Invalid subscription type '{type}': wildcards are only allowed as '.*' suffix",
                    nameof(type));
            }
        }
    }
}

public sealed record Subscription(
    string Type,
    object Handler,
    DispatchDelegate Dispatch);

public delegate Task DispatchDelegate(object handler, CloudEvent evt, CancellationToken ct);
