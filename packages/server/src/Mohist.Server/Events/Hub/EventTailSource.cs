using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.Events.Hub;

/// <summary>
/// Bus subscription that drives the project-scoped live event tail
/// endpoint (<c>GET /api/projects/{projectRef}/events/tail</c>).
/// Subscribes to <c>com.mohist.*</c> and forwards every envelope to
/// <see cref="Publish"/>, which applies strict project isolation (envelope
/// <c>projectid</c> must equal the tail's project; absent ⇒ skip) and the
/// per-subscription compiled <see cref="EventMatchExpression"/> before
/// <c>TryWrite</c>'ing to each open tail channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate handler from <see cref="EventBridge"/>.</b> The
/// SignalR Web hub shares a permissive dispatcher gate
/// (<c>UserNotificationDispatcher</c> project filter) so cross-project
/// admin views see events. The tail requires strict isolation —
/// unprojected events are never delivered — and must not pollute the Web
/// hub's connection model with transient operator state. This handler
/// owns its own strictly-scoped fan-out table.
/// </para>
/// <para>
/// <b>Non-blocking delivery.</b> Every open tail owns a bounded
/// <see cref="Channel{T}"/>; <c>TryWrite</c> drops on a full channel
/// (matching best-effort / no-replay / no-durability contract) so this
/// handler can never block the dispatcher fan-out on a slow consumer.
/// </para>
/// <para>
/// <b>Process-local seam.</b> <see cref="ActiveSubscriptionCount"/>
/// reflects tails opened against this in-process singleton. Multi-silo
/// tailing is a known limitation.
/// </para>
/// </remarks>
[Subscription(Type = "com.mohist.*")]
public sealed class EventTailSource : ICloudEventHandler, IEventTailSource
{
    private const int DefaultTailCapacity = 256;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, SubscriptionEntry> _subscriptions = new();
    private readonly ILogger<EventTailSource> _log;

    public EventTailSource(ILogger<EventTailSource> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool Filter(CloudEvent evt) => evt is not null;

    public Task HandleAsync(CloudEvent cloudEvent, CancellationToken ct)
    {
        if (cloudEvent is null)
            return Task.CompletedTask;

        try
        {
            Publish(cloudEvent);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "EventTailSource failed to dispatch {Type} {EventId}",
                cloudEvent.Type, cloudEvent.Id);
        }
        return Task.CompletedTask;
    }

    public EventTailSubscription Open(string projectId, EventMatchExpression? match)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var id = Guid.NewGuid();
        var entry = new SubscriptionEntry(
            ProjectId: projectId,
            Match: match,
            Channel: Channel.CreateBounded<CloudEvent>(new BoundedChannelOptions(DefaultTailCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            }));
        lock (_gate)
        {
            _subscriptions.Add(id, entry);
        }
        return new EventTailSubscription(entry.Channel, () => Release(id));
    }

    public void Publish(CloudEvent envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CloudEventLineage.TryReadProjectId(envelope.Extensions, out var eventProjectId))
        {
            return;
        }

        SubscriptionEntry[] snapshot;
        lock (_gate)
        {
            if (_subscriptions.Count == 0)
                return;
            snapshot = _subscriptions.Values.ToArray();
        }

        foreach (var entry in snapshot)
        {
            if (!string.Equals(entry.ProjectId, eventProjectId, StringComparison.Ordinal))
                continue;

            if (entry.Match is not null && !entry.Match.Matches(new CloudEventEventMatchInput(envelope)))
                continue;

            entry.Channel.Writer.TryWrite(envelope);
        }
    }

    public int ActiveSubscriptionCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    private void Release(Guid id)
    {
        lock (_gate)
        {
            _subscriptions.Remove(id);
        }
    }

    private sealed record SubscriptionEntry(
        string ProjectId,
        EventMatchExpression? Match,
        Channel<CloudEvent> Channel);
}