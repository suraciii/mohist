using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Specs for the <c>transactional-event-append</c> requirement on the
/// AgentSession producer. Covers issue-361 T-005 scenarios: session
/// state and emitted lifecycle events commit atomically; an event-row
/// write failure rolls back the state transaction and propagates (no
/// swallow-InvalidOperationException or log-and-swallow catch remains);
/// durable event rows survive a crash-after-commit and remain readable
/// on a fresh <c>DbContext</c>. Lifecycle events are stamped with
/// <c>subject = session id</c> and the <c>/mohist/agent-session/{id}</c>
/// source, and per design.md#OQ1 all six lifecycle types are persisted
/// (no filtering); per design.md#OQ3 <c>projectid</c> is not stamped
/// on agent-session events.
/// </summary>
[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class AgentSessionTransactionalEventAppendSpecs : IAsyncLifetime
{
    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private EventStore _eventStore = null!;

    public AgentSessionTransactionalEventAppendSpecs()
    {
        var connectionString = $"Data Source=agent-session-transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _dbFactory = new Factory(_options);

        using (var db = new MohistDbContext(_options))
        {
            db.Database.EnsureCreated();
        }
        _eventStore = new EventStore(_dbFactory, NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsStateAndLifecycleEventRowsTogether()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore);
        var session = BuildSession("agent_txn_ok");

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
        ]);

        var stored = await _eventStore.ListAgentSessionEventsAsync("agent_txn_ok");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound);
        Assert.Contains(stored, s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded);

        var loaded = await store.LoadAsync("agent_txn_ok");
        Assert.NotNull(loaded);
        Assert.Equal("agent_txn_ok", loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_EventRowWriteFailure_RollsBackStateAndEvents_AndDoesNotSwallow()
    {
        var store = new AgentSessionStore(_dbFactory, new ThrowingEventStore());
        var session = BuildSession("agent_txn_fail");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(session.Id, session, [
                new AgentSessionRuntimeBound("acp-1", null),
                new AgentSessionUsageRecorded(new AgentUsageSummary()),
            ]));
        Assert.Contains("event write failed", ex.Message);

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.AgentSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.AgentSessionEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_CrashAfterCommit_LifecycleEventRowsRemainDurableOnFreshDbContext()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore);
        var session = BuildSession("agent_txn_crash");

        await store.SaveAsync(session.Id, session, [new AgentSessionRuntimeBound("acp-1", null)]);

        await using var freshDb = new MohistDbContext(_options);
        var rows = await freshDb.AgentSessionEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/agent-session/agent_txn_crash")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(EventCatalog.ReverseDns.AgentSessionRuntimeBound, row.Type);
        Assert.Null(row.DispatchedAt);
        Assert.Equal("agent_txn_crash", row.Subject);

        var sessionRow = Assert.Single(await freshDb.AgentSessions.AsNoTracking()
            .Where(r => r.Id == "agent_txn_crash")
            .ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(sessionRow.State));
    }

    [Fact]
    public async Task SaveAsync_StampsSourceAndSubjectOnEveryLifecycleEnvelope()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore);
        var session = BuildSession("agent_txn_identity");

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
            new AgentSessionModelChanged("anthropic/claude"),
            new AgentSessionContextCompacted(null, null, null, "summary", "summary text", DateTime.UtcNow),
            new AgentSessionContextExhausted("context_exhaustion", 96d, 960, 1000, DateTime.UtcNow),
            new AgentSessionContextHealthUpdated("yellow", 65d, 650, 1000, DateTime.UtcNow),
        ]);

        var stored = await _eventStore.ListAgentSessionEventsAsync("agent_txn_identity");
        Assert.Equal(6, stored.Count);
        foreach (var entry in stored)
        {
            Assert.Equal("/mohist/agent-session/agent_txn_identity", entry.Envelope.Source.ToString());
            Assert.Equal("agent_txn_identity", entry.Envelope.Subject);
            // Per design.md#OQ3 the agent-session row does not stamp
            // projectid (no consumer reads it from lifecycle events).
            Assert.False(entry.Envelope.Extensions.ContainsKey("projectid"));
        }
    }

    [Fact]
    public async Task SaveAsync_NoEvents_StillCommitsStateRow()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore);
        var session = BuildSession("agent_txn_state_only");

        await store.SaveAsync(session.Id, session, []);

        var loaded = await store.LoadAsync("agent_txn_state_only");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListAgentSessionEventsAsync("agent_txn_state_only"));
    }

    [Fact]
    public async Task SaveAsync_NoEventsOverload_RemainsForCallerWithNoPendingEvents()
    {
        // The grain takes the events-aware overload when there are
        // pending events, but the no-events overload of SaveAsync must
        // still exist for callers that haven't recorded any domain
        // events (e.g. an OpenAsync that does not transition). It
        // continues to commit the state row cleanly.
        var store = new AgentSessionStore(_dbFactory, _eventStore);
        var session = BuildSession("agent_txn_no_events_path");

        await store.SaveAsync(session.Id, session);

        var loaded = await store.LoadAsync("agent_txn_no_events_path");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListAgentSessionEventsAsync("agent_txn_no_events_path"));
    }

    private static AgentSession BuildSession(string id)
    {
        var session = new AgentSession
        {
            Id = id,
            Runtime = new AgentSessionRuntime("runner-1", null),
            Settings = new AgentSessionSettings("opencode"),
        };
        session.Status = session.Status with
        {
            CreatedAt = DateTime.UtcNow,
            LastDataAt = DateTime.UtcNow,
        };
        return session;
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// <see cref="IEventStore"/> that throws on the second append in a
    /// save transaction, simulating an event-row write failure (e.g.
    /// constraint violation). Used to verify that the store does NOT
    /// swallow the exception and that the session state transaction
    /// is rolled back. Mirrors the WorkflowRunStore and IssueStore
    /// equivalents from T-003/T-004.
    /// </summary>
    private sealed class ThrowingEventStore : IEventStore
    {
        private int _callCount;

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount >= 2)
            {
                throw new InvalidOperationException("simulated event write failed");
            }
            await db.AgentSessionEvents.AddAsync(new AgentSessionEventRow
            {
                Id = _callCount,
                Source = envelope.Source.ToString(),
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
                ExtensionsJson = "{}",
            }, ct);
        }

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task MarkDispatchedAsync(string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
