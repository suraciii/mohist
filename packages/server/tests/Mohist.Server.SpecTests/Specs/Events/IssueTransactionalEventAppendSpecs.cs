using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Events;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class IssueTransactionalEventAppendSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj_issue_txn";
    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly NullDispatchGrainFactory _grainFactory = new();
    private EventStore _eventStore = null!;

    public IssueTransactionalEventAppendSpecs()
    {
        var connectionString = $"Data Source=issue-transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
    public async Task SaveAsync_CommitsIssueStateAndOwnEventsTogether()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(1);

        await store.SaveAsync(Key(1), issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var events = await _eventStore.ListIssueEventsAsync(ProjectId, 1);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueCreated);
        Assert.Contains(events, entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueArchived);

        var loaded = await store.LoadAsync(Key(1));
        Assert.NotNull(loaded);
        Assert.Equal(ProjectId, loaded!.ProjectId);
        Assert.Equal(1, loaded.Number);
    }

    [Fact]
    public async Task SaveAsync_EventWriteFailureRollsBackIssueStateAndEvents()
    {
        var store = CreateStore(new ThrowingEventStore());
        var issue = BuildIssue(2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(Key(2), issue, [
                new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
                new IssueArchived(),
            ]));

        Assert.Contains("event write failed", exception.Message);
        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.Issues.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.IssueEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_PersistsScopedEventSourceAcrossDbContexts()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(3);

        await store.SaveAsync(Key(3), issue, [new IssueArchived()]);

        await using var fresh = new MohistDbContext(_options);
        var row = Assert.Single(await fresh.IssueEvents.AsNoTracking().ToListAsync());
        Assert.Equal($"/mohist/projects/{ProjectId}/issues/3", row.Source);
        Assert.Equal(EventCatalog.ReverseDns.IssueArchived, row.Type);
        Assert.Null(row.DispatchedAt);
        var state = Assert.Single(await fresh.Issues.AsNoTracking().ToListAsync());
        Assert.Equal(ProjectId, state.ProjectId);
        Assert.Equal(3, state.Number);
    }

    [Fact]
    public async Task SaveAsync_StampsOnlyIssueOwnedLineage()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(4, epicNumber: 7);

        await store.SaveAsync(Key(4), issue, [new IssueArchived()]);

        var stored = Assert.Single(await _eventStore.ListIssueEventsAsync(ProjectId, 4));
        Assert.Equal(ProjectId, stored.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("4", stored.Envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal("7", stored.Envelope.Extensions[EventCatalog.Lineage.Epic]);
        EnvelopeConformance.AssertRequired(stored.Envelope);
    }

    [Fact]
    public void IssueLineage_OmitsEpicWhenIssueHasNoEpicNumber()
    {
        var extensions = IssueLineage.BuildExtensions(BuildIssue(5));

        Assert.Equal(ProjectId, extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("5", extensions[EventCatalog.Lineage.Issue]);
        Assert.False(extensions.ContainsKey(EventCatalog.Lineage.Epic));
    }

    [Fact]
    public async Task SaveAsync_WithoutEventsStillPersistsIssueState()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(6);

        await store.SaveAsync(Key(6), issue);

        Assert.NotNull(await store.LoadAsync(Key(6)));
        Assert.Empty(await _eventStore.ListIssueEventsAsync(ProjectId, 6));
    }

    private IssueStore CreateStore(IEventStore events) =>
        new(_dbFactory, events, _grainFactory, NullLogger<IssueStore>.Instance);

    private static string Key(int number) => GrainKey.Issue(new IssueKey(ProjectId, number));

    private static DomainIssue BuildIssue(int number, int? epicNumber = null) => new()
    {
        ProjectId = ProjectId,
        Number = number,
        Title = "Transaction probe",
        Priority = "p2",
        EpicNumber = epicNumber,
    };

    private sealed class Factory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }

    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException();
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix) => throw new NotSupportedException();
        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) => throw new NotSupportedException();
        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));
        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
        public GrainId GrainId => default;
        public string Key => string.Empty;
    }

    private sealed class ThrowingEventStore : IEventStore
    {
        private int _callCount;

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount >= 2) throw new InvalidOperationException("simulated event write failed");
            await db.IssueEvents.AddAsync(new IssueEventRow
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

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
