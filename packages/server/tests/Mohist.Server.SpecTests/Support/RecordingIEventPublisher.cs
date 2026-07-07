using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Test-only <see cref="IEventPublisher"/> decorator that records every
/// <c>PublishAsync</c> call into an in-memory list and forwards to the
/// inner publisher. Used by spec tests that need to assert on emit
/// counts (e.g. lifecycle event dedup in
/// <c>AgentSessionLifecycleDedupSpecs</c>) without modifying the
/// production bus or relying on shared mutable state in
/// <c>MohistIntegrationFixture</c>.
/// </summary>
public sealed class RecordingIEventPublisher : IEventPublisher
{
    private readonly IEventPublisher _inner;
    private readonly List<RecordedPublish> _published = [];
    private readonly object _gate = new();

    public RecordingIEventPublisher(IEventPublisher inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<RecordedPublish> Published
    {
        get
        {
            lock (_gate)
            {
                return _published.ToArray();
            }
        }
    }

    public int CountOfType(string type)
    {
        lock (_gate)
        {
            return _published.Count(p => string.Equals(p.Type, type, StringComparison.Ordinal));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _published.Clear();
        }
    }

    public async Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        var record = new RecordedPublish(envelope.Type, envelope.Source.ToString(), envelope.Subject, envelope.Data);
        lock (_gate)
        {
            _published.Add(record);
        }

        await _inner.PublishAsync(envelope, ct);
    }

    public async Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default)
    {
        RecordedPublish record;
        if (data is not null)
        {
            var element = System.Text.Json.JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions);
            record = new RecordedPublish(type, source, subject, element);
        }
        else
        {
            record = new RecordedPublish(type, source, subject, null);
        }

        lock (_gate)
        {
            _published.Add(record);
        }

        await _inner.PublishAsync(data, type, source, subject, extensions, ct);
    }

    public sealed record RecordedPublish(string Type, string Source, string? Subject, System.Text.Json.JsonElement? Data);
}
