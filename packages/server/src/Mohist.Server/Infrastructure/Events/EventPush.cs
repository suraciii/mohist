using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Events.Grains;

namespace Mohist.Server.Infrastructure.Events;

public interface ICloudEventPushHandler
{
    bool Filter(CloudEvent evt);
    Task HandleAsync(CloudEvent evt, CancellationToken ct);
}

public sealed record EventPushSubscription(
    string Type,
    object Handler,
    DispatchDelegate Dispatch,
    string Identity);

public interface IEventPushQueue
{
    bool TryEnqueue(CloudEvent evt);
}

public sealed class EventPushQueue : IEventPushQueue
{
    private readonly Channel<CloudEvent> _channel;
    private readonly ILogger<EventPushQueue> _log;

    public EventPushQueue(IOptions<EventDispatcherOptions> options, ILogger<EventPushQueue> log)
    {
        var capacity = options?.Value.PushQueueCapacity
            ?? throw new ArgumentNullException(nameof(options));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "PushQueueCapacity must be positive");

        _channel = Channel.CreateBounded<CloudEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _log = log;
    }

    public bool TryEnqueue(CloudEvent evt)
    {
        if (_channel.Writer.TryWrite(evt))
            return true;

        _log.LogWarning("Event push queue is full; dropping {Type} {EventId}", evt.Type, evt.Id);
        return false;
    }

    internal IAsyncEnumerable<CloudEvent> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

public sealed class EventPushWorker : BackgroundService
{
    private readonly EventPushQueue _queue;
    private readonly IReadOnlyList<EventPushSubscription> _subscriptions;
    private readonly TimeProvider _time;
    private readonly EventDispatcherOptions _options;
    private readonly ILogger<EventPushWorker> _log;

    public EventPushWorker(
        EventPushQueue queue,
        IEnumerable<EventPushSubscription> subscriptions,
        TimeProvider time,
        IOptions<EventDispatcherOptions> options,
        ILogger<EventPushWorker> log)
    {
        _queue = queue;
        _subscriptions = subscriptions.ToList();
        _time = time;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await DeliverAsync(evt, stoppingToken).ConfigureAwait(false);
    }

    internal async Task DeliverAsync(CloudEvent evt, CancellationToken stoppingToken)
    {
        foreach (var subscription in _subscriptions)
        {
            if (!CloudEventTypeMatcher.Matches(subscription.Type, evt.Type))
                continue;

            using var timeout = new TimeProviderCancellation(_time, _options.PushDeliveryTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, timeout.CancellationToken);
            try
            {
                await subscription.Dispatch(subscription.Handler, evt, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.CancellationToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Event push handler {Handler} timed out for {Type} {EventId}",
                    subscription.Identity,
                    evt.Type,
                    evt.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Event push handler {Handler} failed for {Type} {EventId}",
                    subscription.Identity,
                    evt.Type,
                    evt.Id);
            }
        }
    }

    private sealed class TimeProviderCancellation : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly ITimer _timer;

        public TimeProviderCancellation(TimeProvider time, TimeSpan timeout)
        {
            _timer = time.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                _source,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        public CancellationToken CancellationToken => _source.Token;

        public void Dispose()
        {
            _timer.Dispose();
            _source.Dispose();
        }
    }
}

public sealed class NullEventPushQueue : IEventPushQueue
{
    public static readonly NullEventPushQueue Instance = new();

    private NullEventPushQueue()
    {
    }

    public bool TryEnqueue(CloudEvent evt) => true;
}
