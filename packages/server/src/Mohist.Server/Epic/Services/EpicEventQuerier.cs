using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Epic.Services;

/// <summary>
/// Read-side service for the epic activity timeline. Wraps
/// <see cref="IEventStore.ListEpicEventsAsync"/> and surfaces the stored
/// CloudEvents envelopes as the wire-level <c>StoredCloudEventDto</c> shape
/// produced by the HTTP route. The querier itself is a thin pass-through —
/// all source isolation, ordering, and persistence guarantees are owned by
/// <see cref="IEventStore"/> so this querier cannot drift away from the
/// shared event-read contract.
/// </summary>
public class EpicEventQuerier : IScopedService
{
    private readonly IEventStore _eventStore;

    public EpicEventQuerier(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(
        string projectId,
        int epicNumber,
        int limit = 200,
        CancellationToken ct = default) =>
        _eventStore.ListEpicEventsAsync(projectId, epicNumber, limit, ct);
}
