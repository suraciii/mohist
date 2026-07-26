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
    }

    public InMemoryEventBus(
        IEnumerable<Subscription> subscriptions,
        IEventStore eventStore,
        TimeProvider time,
        ILogger<InMemoryEventBus> log)
        : this(eventStore, time, log)
    {
        var count = 0;
        foreach (var sub in subscriptions)
        {
            CloudEventTypeMatcher.ValidatePattern(sub.Type);
            count++;
        }
        log.LogInformation("Event bus ready: {Count} subscriptions", count);
    }

    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        return _eventStore.AppendAsync(envelope, ct);
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

}

public sealed record Subscription
{
    public string Type { get; init; }
    public object Handler { get; init; }
    public DispatchDelegate Dispatch { get; init; }
    public string Identity { get; init; }

    public Subscription(string type, object handler, DispatchDelegate dispatch)
        : this(type, handler, dispatch, ResolveIdentity(handler))
    {
    }

    public Subscription(string type, object handler, DispatchDelegate dispatch, string identity)
    {
        Type = type;
        Handler = handler;
        Dispatch = dispatch;
        Identity = identity;
    }

    private static string ResolveIdentity(object handler) =>
        handler.GetType().FullName ?? handler.GetType().Name;
}

public delegate Task DispatchDelegate(object handler, CloudEvent evt, CancellationToken ct);
