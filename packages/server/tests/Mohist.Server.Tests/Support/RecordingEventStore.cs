using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Tests.Support;

public class RecordingEventStore : IEventStore
{
    private readonly List<RecordedEnvelope> _events = [];
    private readonly Lock _gate = new();

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(new RecordedEnvelope(envelope));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/workflow-runs/{workflowRunId}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    private sealed record RecordedEnvelope(CloudEvent Envelope);
}
