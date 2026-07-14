using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[CollectionDefinition("EpicRecovery")]
public sealed class EpicRecoveryCollection : ICollectionFixture<EpicRecoveryFixture>
{
}

[Collection("EpicRecovery")]
public class EpicRecoverySpecs
{
    private const string ProjectId = "project_recovery";
    private const string EpicId = "epic_recovery";
    private const string IssueId = "issue_recovery";

    private readonly EpicRecoveryFixture _fixture;

    public EpicRecoverySpecs(EpicRecoveryFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueLinkedEvent_DispatcherConvergesWhenInlineRecomputeWasSkipped()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedEpicAsync(ProjectId, EpicId, "running");
        await _fixture.SeedIssueAsync(ProjectId, IssueId, 1, IssueStatus.Done);

        await using (var db = _fixture.CreateDbContext())
        {
            db.EpicIssues.Add(new EpicIssueRow
            {
                ProjectId = ProjectId,
                EpicId = EpicId,
                IssueId = IssueId,
                IssueNumber = 1,
                CreatedAt = _fixture.TimeProvider.GetUtcNow(),
            });
            await _fixture.EventStore.AppendAsync(db, EpicEvent(ProjectId, EpicId,
                EventCatalog.ReverseDns.EpicIssueLinked, new EpicIssueLinked(IssueId, 1)));
            await db.SaveChangesAsync();
        }

        var linked = Assert.Single(
            await _fixture.EventStore.ListUndeliveredAsync(),
            evt => evt.Type == EventCatalog.ReverseDns.EpicIssueLinked);

        await _fixture.Dispatcher.DispatchAsync(CancellationToken.None);
        await _fixture.Dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal("done", await _fixture.GetEpicStatusAsync(ProjectId, EpicId));
        Assert.True(await _fixture.IsDispatchedAsync(linked.Origin, linked.Source, linked.Id));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandStartFailure_PersistsRecoveryEventsAndDispatcherConverges(bool resume)
    {
        await _fixture.ResetAsync();
        await _fixture.SeedEpicAsync(ProjectId, EpicId, resume ? "paused" : "idle");
        await _fixture.SeedIssueAsync(ProjectId, IssueId, 1, IssueStatus.Backlog);
        await _fixture.SeedLinkAsync(ProjectId, EpicId, IssueId, 1);

        var grain = new EpicGrain(
            _fixture.DbFactory,
            new StartFailingGrainFactory(),
            _fixture.TimeProvider,
            _fixture.EventStore,
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:{EpicId}",
        };

        if (resume)
            await grain.ResumeAsync();
        else
            await grain.StartAsync();

        var recoveryEvents = (await _fixture.EventStore.ListUndeliveredAsync())
            .Where(evt => evt.Source == EpicEventPersistence.EpicSource(EpicId))
            .Where(evt => evt.Type is EventCatalog.ReverseDns.EpicStatusChanged or EventCatalog.ReverseDns.EpicStartAttemptFailed)
            .ToList();
        Assert.Contains(recoveryEvents, evt => evt.Type == EventCatalog.ReverseDns.EpicStatusChanged);
        Assert.Contains(recoveryEvents, evt => evt.Type == EventCatalog.ReverseDns.EpicStartAttemptFailed);

        await _fixture.SetIssueStatusAsync(ProjectId, IssueId, IssueStatus.Done);
        await _fixture.Dispatcher.DispatchAsync(CancellationToken.None);
        await _fixture.Dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal("done", await _fixture.GetEpicStatusAsync(ProjectId, EpicId));
        foreach (var recoveryEvent in recoveryEvents)
            Assert.True(await _fixture.IsDispatchedAsync(recoveryEvent.Origin, recoveryEvent.Source, recoveryEvent.Id));
        await using var verify = _fixture.CreateDbContext();
        Assert.Empty(await verify.DeadLetters.AsNoTracking().ToListAsync());
    }

    private CloudEvent EpicEvent(string projectId, string epicId, string type, object data) => new(
        id: Guid.NewGuid().ToString(),
        source: new Uri(EpicEventPersistence.EpicSource(epicId), UriKind.Relative),
        type: type,
        time: _fixture.TimeProvider.GetUtcNow(),
        data: System.Text.Json.JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions),
        subject: "1",
        extensions: new Dictionary<string, string>
        {
            ["projectid"] = projectId,
            ["epicid"] = epicId,
            ["epicno"] = "1",
        });

    private sealed class StartFailingGrainFactory : IGrainFactory
    {
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)new StartFailingIssueGrain();
            throw new NotSupportedException(typeof(TGrainInterface).Name);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    private sealed class StartFailingIssueGrain : IIssueGrain
    {
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) =>
            throw new InvalidOperationException("simulated StartWorkAsync failure");
        public Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null) => throw new NotSupportedException();
        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
    }
}

public sealed class EpicRecoveryFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;

    public InProcessTestCluster Cluster { get; private set; } = null!;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
    public IDbContextFactory<MohistDbContext> DbFactory { get; private set; } = null!;
    public EventStore EventStore { get; private set; } = null!;
    public EventDispatcherService Dispatcher => Cluster.GetSiloServiceProvider(null).GetRequiredService<EventDispatcherService>();

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=mohist-epic-recovery-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        MigratedSqliteTemplate.CopyTo(_keeper);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        DbFactory = new RecoveryDbContextFactory(options);
        EventStore = new EventStore(DbFactory, NullLogger<EventStore>.Instance);

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connectionString));
            siloBuilder.Services.AddSingleton<IEventStore, EventStore>();
            siloBuilder.Services.AddSingleton<IDeadLetterStore, DeadLetterStore>();
            siloBuilder.Services.AddCloudEventHandlers([
                typeof(EpicIssueLinkedHandler),
                typeof(EpicRunningStatusHandler),
                typeof(EpicStartRetryHandler),
            ]);
            siloBuilder.Services.AddSingleton<EventDispatcherService>();
            siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider);
            siloBuilder.Services.Configure<EventDispatcherOptions>(options =>
            {
                options.BatchSize = 100;
                options.MaxAttempts = 2;
                options.BaseBackoff = TimeSpan.Zero;
                options.MaxBackoff = TimeSpan.Zero;
            });
        });
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    public MohistDbContext CreateDbContext() => DbFactory.CreateDbContext();

    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DeadLetters\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"EpicEvents\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"EpicActiveIssues\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"EpicIssues\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Issues\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Epics\"");
    }

    public async Task SeedEpicAsync(string projectId, string epicId, string status)
    {
        await using var db = CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = 1,
            Title = "Recovery epic",
            Description = "",
            Priority = "p2",
            Status = status,
            CreatedAt = TimeProvider.GetUtcNow(),
            UpdatedAt = TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedIssueAsync(string projectId, string issueId, int number, IssueStatus status)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = "Recovery issue",
            Priority = "p2",
            Status = status,
        };
        await using var db = CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedLinkAsync(string projectId, string epicId, string issueId, int issueNumber)
    {
        await using var db = CreateDbContext();
        db.EpicIssues.Add(new EpicIssueRow
        {
            ProjectId = projectId,
            EpicId = epicId,
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    public async Task SetIssueStatusAsync(string projectId, string issueId, IssueStatus status)
    {
        await using var db = CreateDbContext();
        var row = await db.Issues.SingleAsync(issue => issue.ProjectId == projectId && issue.IssueId == issueId);
        var issue = IssueStore.Deserialize(row.State) ?? throw new InvalidOperationException("Issue state was missing.");
        if (status != IssueStatus.Done)
            throw new ArgumentOutOfRangeException(nameof(status));
        issue.Start("wr_recovery", null, TimeProvider.GetUtcNow().UtcDateTime);
        issue.Complete("wr_recovery", TimeProvider.GetUtcNow().UtcDateTime);
        issue.ClearPendingEvents();
        row.State = IssueStore.Serialize(issue);
        await db.SaveChangesAsync();
    }

    public async Task<string> GetEpicStatusAsync(string projectId, string epicId)
    {
        await using var db = CreateDbContext();
        return await db.Epics.AsNoTracking()
            .Where(epic => epic.ProjectId == projectId && epic.Id == epicId)
            .Select(epic => epic.Status)
            .SingleAsync();
    }

    public async Task<bool> IsDispatchedAsync(EventOrigin origin, string source, long id)
    {
        await using var db = CreateDbContext();
        return origin switch
        {
            EventOrigin.Epic => await db.EpicEvents.AsNoTracking()
                .AnyAsync(evt => evt.Source == source && evt.Id == id && evt.DispatchedAt != null),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
    }

    private sealed class RecoveryDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public RecoveryDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            _options = options;
        }

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
