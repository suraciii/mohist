using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Orleans;
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
    private readonly NullDispatchGrainFactory _grainFactory = new();
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

        MigratedSqliteTemplate.CopyTo(_keeper);
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
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
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
        var store = new AgentSessionStore(_dbFactory, new ThrowingEventStore(), _grainFactory, NullLogger<AgentSessionStore>.Instance);
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
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
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
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_identity");

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
            new AgentSessionModelChanged("anthropic/claude"),
            new AgentSessionContextCompacted(null, null, null, "summary", "summary text", TestTime.UtcDateTime),
            new AgentSessionContextExhausted("context_exhaustion", 96d, 960, 1000, TestTime.UtcDateTime),
            new AgentSessionContextHealthUpdated("yellow", 65d, 650, 1000, TestTime.UtcDateTime),
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
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
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
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
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
            CreatedAt = TestTime.UtcDateTime,
            LastDataAt = TestTime.UtcDateTime,
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
    /// Minimal <see cref="IGrainFactory"/> stand-in for transactional
    /// unit specs. The dispatcher is a no-op grain reference; producers
    /// only need to call DispatchNowAsync without exceptions. Lets the
    /// store exercise its post-commit poke code path without spinning up
    /// an Orleans silo.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Drop-in <see cref="IEventDispatcherGrain"/> reference whose
    /// <see cref="DispatchNowAsync"/> returns <see cref="Task.CompletedTask"/>.
    /// Lets the post-commit poke fire without an Orleans silo.
    /// </summary>
    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

        public GrainId GrainId => default;
        public string Key => string.Empty;
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

        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
