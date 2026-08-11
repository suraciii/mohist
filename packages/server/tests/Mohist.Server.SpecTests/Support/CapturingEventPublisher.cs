using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Minimal event publisher for the dispatcher fixture. It forwards every
/// envelope to the fixture's in-memory event store.
/// </summary>
public sealed class CapturingEventPublisher : IEventPublisher
{
    private readonly List<CloudEvent> _published = [];
    private IEventStore? _sink;
    private readonly object _gate = new();

    public void RegisterSink(IEventStore sink)
    {
        lock (_gate) { _sink = sink; }
    }

    public IReadOnlyList<CloudEvent> Published
    {
        get { lock (_gate) { return _published.ToList(); } }
    }

    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _published.Add(envelope);
            return _sink?.AppendAsync(envelope, ct) ?? Task.CompletedTask;
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
        var dataJson = System.Text.Json.JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions);
        var extDict = extensions is null
            ? null
            : new Dictionary<string, string>(extensions, StringComparer.Ordinal);
        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.RelativeOrAbsolute),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: dataJson,
            subject: subject,
            extensions: extDict);
        await PublishAsync(envelope, ct).ConfigureAwait(false);
    }
}
