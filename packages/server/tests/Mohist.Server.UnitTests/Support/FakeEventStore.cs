using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.UnitTests.Support;

/// <summary>
/// Programmable <see cref="IEventStore"/> fake for dispatcher unit tests.
/// Maintains an internal "undelivered queue" (call <see cref="Enqueue"/>
/// before each <see cref="EventDispatcherService.DispatchAsync"/>) and
/// records <see cref="MarkDispatchedAsync"/> invocations so tests can
/// assert the post-dispatch state of every row. Designed to be the only
/// seam — no other fake is needed to drive the dispatch loop.
///
/// Behavior knobs:
/// <list type="bullet">
///   <item><see cref="ThrowOnMark"/> — when set, <c>MarkDispatchedAsync</c>
///     throws. Used to simulate the deliver-before-mark crash scenario.</item>
///   <item><see cref="ThrowOnList"/> — when set, <c>ListUndeliveredAsync</c>
///     throws. Verifies graceful pull-failure handling.</item>
/// </list>
/// </summary>
public sealed class FakeEventStore : IEventStore
{
    private readonly object _gate = new();
    private readonly List<UndeliveredEvent> _undelivered = [];
    private readonly List<RecordedDispatch> _marked = [];

    public Func<UndeliveredEvent, bool>? ThrowOnMark { get; set; }
    public Func<int, bool>? ThrowOnList { get; set; }

    /// <summary>
    /// Stages an undelivered event row for the next
    /// <see cref="ListUndeliveredAsync"/> call. Tests should Enqueue
    /// rows before driving the dispatcher and assert on
    /// <see cref="Marked"/> afterwards. Multiple Enqueues accumulate.
    /// </summary>
    public void Enqueue(UndeliveredEvent evt)
    {
        lock (_gate)
        {
            _undelivered.Add(evt);
        }
    }

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task MarkDispatchedAsync(
        EventOrigin origin,
        string source,
        long id,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        if (ThrowOnMark is not null)
        {
            lock (_gate)
            {
                var match = _undelivered.FirstOrDefault(e =>
                    e.Origin == origin && e.Source == source && e.Id == id);
                if (match is not null && ThrowOnMark(match))
                    throw new InvalidOperationException(
                        $"simulated mark-dispatched failure for {source}/{id}");
            }
        }
        lock (_gate)
        {
            _marked.Add(new RecordedDispatch(origin, source, id, dispatchedAt));
            _undelivered.RemoveAll(e => e.Origin == origin && e.Source == source && e.Id == id);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
    {
        if (ThrowOnList?.Invoke(limit) == true)
            throw new InvalidOperationException("simulated list-undelivered failure");
        lock (_gate)
        {
            // Real EventStore.ListUndeliveredAsync returns rows already
            // sorted by (Source, Id). We mimic that order so the test
            // surface matches the production contract.
            var rows = _undelivered
                .OrderBy(e => e.Source, StringComparer.Ordinal)
                .ThenBy(e => e.Id)
                .Take(limit)
                .Select(e => e)
                .ToList();
            return Task.FromResult<IReadOnlyList<UndeliveredEvent>>(rows);
        }
    }

    public IReadOnlyList<UndeliveredEvent> PendingUndelivered
    {
        get
        {
            lock (_gate)
            {
                return _undelivered
                    .OrderBy(e => e.Source, StringComparer.Ordinal)
                    .ThenBy(e => e.Id)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<RecordedDispatch> Marked
    {
        get
        {
            lock (_gate)
            {
                return _marked.ToList();
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate) { return _undelivered.Count; }
        }
    }

    internal StateSnapshot CaptureState()
    {
        lock (_gate)
        {
            return new StateSnapshot(_undelivered.ToList(), _marked.ToList());
        }
    }

    internal void RestoreState(StateSnapshot snapshot)
    {
        lock (_gate)
        {
            _undelivered.Clear();
            _undelivered.AddRange(snapshot.Undelivered);
            _marked.Clear();
            _marked.AddRange(snapshot.Marked);
        }
    }

    internal sealed record StateSnapshot(
        IReadOnlyList<UndeliveredEvent> Undelivered,
        IReadOnlyList<RecordedDispatch> Marked);

    /// <summary>
    /// Simulates <c>DeadLetterStore.RetryAsync</c>: re-queues a row that
    /// was previously marked dispatched so the next <see cref="ListUndeliveredAsync"/>
    /// returns it. Mirrors the production path where the source event's
    /// <c>DispatchedAt</c> is re-nulled.
    /// </summary>
    public void ReQueueForRedelivery(string origin, string source, long id)
    {
        var originEnum = ParseOrigin(origin);
        lock (_gate)
        {
            var prior = _marked.FirstOrDefault(r =>
                r.Origin == originEnum && r.Source == source && r.Id == id);
            _marked.RemoveAll(r => r.Origin == originEnum && r.Source == source && r.Id == id);
            var existing = _undelivered.FirstOrDefault(e =>
                e.Origin == originEnum && e.Source == source && e.Id == id);
            if (existing is null)
            {
                _undelivered.Add(new UndeliveredEvent(
                    Origin: originEnum,
                    Id: id,
                    Source: source,
                    EventId: $"evt-retry-{id}",
                    Type: "com.mohist.retry",
                    Time: prior?.DispatchedAt ?? DateTimeOffset.UnixEpoch,
                    SpecVersion: "1.0",
                    Subject: null,
                    DataContentType: "application/json",
                    Data: JsonDocument.Parse("null").RootElement,
                    ExtensionsJson: "{}"));
            }
        }
    }

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    public sealed record RecordedDispatch(
        EventOrigin Origin,
        string Source,
        long Id,
        DateTimeOffset DispatchedAt);

    /// <summary>
    /// Builds an <see cref="UndeliveredEvent"/> with sensible defaults for
    /// tests that don't care about every field. Defaults to
    /// <see cref="EventOrigin.Issue"/> so dispatched-at can be observed
    /// without configuring four origins.
    /// </summary>
    public static UndeliveredEvent Build(
        string type,
        string source,
        long id = 1,
        EventOrigin origin = EventOrigin.Issue,
        string eventId = "evt-1",
        JsonElement? data = null,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null)
    {
        var extensionsJson = extensions is null
            ? "{}"
            : JsonSerializer.Serialize(extensions, CloudEvent.JsonOptions);
        return new UndeliveredEvent(
            Origin: origin,
            Id: id,
            Source: source,
            EventId: eventId,
            Type: type,
            Time: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            SpecVersion: "1.0",
            Subject: subject,
            DataContentType: "application/json",
            Data: data ?? JsonDocument.Parse("null").RootElement,
            ExtensionsJson: extensionsJson);
    }
}
