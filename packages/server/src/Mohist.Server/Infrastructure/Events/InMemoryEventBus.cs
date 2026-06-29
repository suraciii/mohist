using System.Text.Json;

namespace Mohist.Server.Infrastructure.Events;

public sealed class InMemoryEventBus : IEventPublisher
{
    private readonly ILogger<InMemoryEventBus> _log = null!;

    public InMemoryEventBus(IEnumerable<Subscription> subscriptions, ILogger<InMemoryEventBus> log)
    {
        _subscriptions = subscriptions.ToList();
        _log = log;

        foreach (var sub in _subscriptions)
            ValidateType(sub.Type);

        log.LogInformation(
            "Event bus ready: {Count} subscriptions", _subscriptions.Count);
    }

    public InMemoryEventBus(ILogger<InMemoryEventBus> log) : this([], log) { }

    private readonly List<Subscription> _subscriptions;

    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        return DispatchAsync(envelope, ct);
    }

    /// <summary>
    /// Adds a subscription after construction. Used by test fixtures that
    /// build the bus empty and then wire the same handlers the production
    /// pipeline registers via <c>AddCloudEventHandlersFromAssembly</c>.
    /// </summary>
    public void AddSubscription(Subscription subscription)
    {
        ValidateType(subscription.Type);
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }
    }

    private IReadOnlyList<Subscription> Snapshot() => _subscriptions.ToList();

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
            time: DateTimeOffset.UtcNow,
            data: dataJson,
            subject: subject,
            extensions: extDict);

        await DispatchAsync(evt, ct).ConfigureAwait(false);
    }

    private async Task DispatchAsync(CloudEvent evt, CancellationToken ct)
    {
        // Snapshot the subscription list so concurrent AddSubscription calls
        // (used by test fixtures that wire handlers post-construction) do not
        // mutate it mid-dispatch.
        var subs = Snapshot();
        foreach (var sub in subs)
        {
            if (!Matches(sub.Type, evt.Type))
                continue;

            try
            {
                await sub.Dispatch(sub.Handler, evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Handler {Handler} failed for event {EventType}",
                    sub.Handler.GetType().Name, evt.Type);
            }
        }
    }

    private static bool Matches(string pattern, string type)
    {
        foreach (var alternative in pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (alternative == type) return true;
            if (alternative == "*") return true;
            if (alternative.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = alternative[..^2];
                if (type == prefix || type.StartsWith(prefix + ".", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
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
