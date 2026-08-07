using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Inbox.Subscriptions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Shared in-memory SQLite and service-scope support for unit specs that
/// exercise <see cref="InboxProjectionHandler"/>. Each test stands up a
/// fresh in-memory database, seeds the issue / workflow-run rows it
/// needs, drives the handler with one CloudEvent envelope, and inspects
/// the resulting inbox row.
/// </summary>
internal static class InboxProjectionTestSupport
{
    public static readonly DateTimeOffset FixedEventTime = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    public static WorkflowRunStore CreateRunStore(
        IDbContextFactory<MohistDbContext> factory,
        IEventStore eventStore) =>
        new(factory, eventStore, new NullEventDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance, TestServices.BackgroundTasks, new DispatchSnapshotStore(factory, NullLogger<DispatchSnapshotStore>.Instance) as IDispatchSnapshotStore);

    public static InboxProjectionHandler CreateHandler(TestSqliteDatabase database) =>
        CreateHandler(database, new NoopEventPublisher());

    public static InboxProjectionHandler CreateHandler(TestSqliteDatabase database, IEventPublisher eventPublisher) =>
        CreateHandler(database, eventPublisher, configureServices: null);

    public static InboxProjectionHandler CreateHandler(
        TestSqliteDatabase database,
        IEventPublisher eventPublisher,
        Action<IServiceCollection>? configureServices) =>
        new(
            scopeFactory: new InboxScopeFactory(database, eventPublisher, configureServices),
            log: NullLogger<InboxProjectionHandler>.Instance);

    public static CloudEvent BuildIssueEvent(string type, string projectId, int issueNumber, string eventId) =>
        new(
            id: eventId,
            source: new Uri($"/mohist/projects/{projectId}/issues/{issueNumber}", UriKind.Relative),
            type: type,
            time: TestTime.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = projectId,
                [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
            });

    public static CloudEvent BuildWorkflowEvent(
        string type,
        string workflowRunId,
        string eventId,
        IReadOnlyDictionary<string, string>? extensions = null,
        string projectId = "proj_a",
        int issueNumber = 1) =>
        new(
            id: eventId,
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: type,
            time: TestTime.UtcNow,
            data: null,
            extensions: extensions ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = projectId,
                [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
                [EventCatalog.Lineage.WorkflowRunId] = workflowRunId,
            });

    public static async Task<List<InboxItemView>> GetInboxAsync(TestSqliteDatabase database, string projectId)
    {
        await using var db = database.CreateContext();
        var rows = await db.InboxItems.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ArchivedAt == null)
            .ToListAsync();
        return rows
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new InboxItemView
            {
                Id = r.Id,
                ProjectId = r.ProjectId,
                IssueNumber = r.IssueNumber,
                IssueTitle = r.IssueTitle,
                NotificationKind = r.NotificationKind,
                SourceEventSource = r.SourceEventSource,
                SourceEventId = r.SourceEventId,
                CreatedAt = r.CreatedAt,
                ReadAt = r.ReadAt,
                ArchivedAt = r.ArchivedAt,
            })
            .ToList();
    }

    public static async Task SeedIssueAsync(
        TestSqliteDatabase database,
        string projectId,
        int issueNumber,
        string title)
    {
        await SeedProjectAsync(database, projectId);

        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = title,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
        };
        await using var db = database.CreateContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedProjectAsync(TestSqliteDatabase database, string projectId)
    {
        await using var db = database.CreateContext();
        if (await db.Projects.AnyAsync(p => p.Id == projectId))
            return;

        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = ProjectNameFromId(projectId),
            RepositoriesJson = """[{"name":"test-repo","gitUrl":"git@example.com:test-repo.git","baseBranch":"main","isDefault":true}]""",
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedSubscriptionAsync(
        TestSqliteDatabase database,
        string projectId,
        bool workflowFailedEnabled = true,
        bool approvalRequestedEnabled = true,
        bool issueStartedEnabled = true,
        bool issueCompletedEnabled = true)
    {
        await SeedProjectAsync(database, projectId);

        var store = new InboxSubscriptionStore(
            new TestDbContextFactory(database.Options),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)));
        await store.SetAsync(projectId, new InboxSubscriptionState(
            WorkflowFailedEnabled: workflowFailedEnabled,
            ApprovalRequestedEnabled: approvalRequestedEnabled,
            IssueStartedEnabled: issueStartedEnabled,
            IssueCompletedEnabled: issueCompletedEnabled));
    }

    private static string ProjectNameFromId(string projectId)
    {
        var candidate = projectId.Replace('_', '-');
        return candidate.Length <= 63 ? candidate : candidate[..63];
    }

    public static async Task SeedWorkflowRunAsync(
        TestSqliteDatabase database,
        string workflowRunId,
        string? projectId,
        int? issueNumber)
    {
        var run = BuildWorkflowRun(workflowRunId, projectId, issueNumber);
        var json = Mohist.Server.Infrastructure.JSON.Serialize(run);
        await using var db = database.CreateContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    public static WorkflowRun BuildWorkflowRun(
        string workflowRunId,
        string? projectId,
        int? issueNumber)
    {
        return new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                ProjectId: projectId,
                IssueNumber: issueNumber),
            Stages = new List<StageRun>(),
        };
    }

    public static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }

    public static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateMigrated();

    public sealed class NoopEventStore : IEventStore
    {
        public Task AppendAsync(CloudEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task AppendAsync(MohistDbContext db, CloudEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>(Array.Empty<UndeliveredEvent>());
    }

    public sealed class NoopEventPublisher : IEventPublisher
    {
        public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PublishAsync<TData>(TData data, string type, string source, string? subject = null, IReadOnlyDictionary<string, string>? extensions = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class InboxScopeFactory : IServiceScopeFactory
    {
        private readonly TestSqliteDatabase _database;
        private readonly IEventPublisher _eventPublisher;
        private readonly Action<IServiceCollection>? _configureServices;

        public InboxScopeFactory(
            TestSqliteDatabase database,
            IEventPublisher eventPublisher,
            Action<IServiceCollection>? configureServices = null)
        {
            _database = database;
            _eventPublisher = eventPublisher;
            _configureServices = configureServices;
        }

        public IServiceScope CreateScope() => new InboxScope(_database, _eventPublisher, _configureServices);

        private sealed class InboxScope : IServiceScope
        {
            private readonly TestSqliteDatabase _database;
            public InboxScope(TestSqliteDatabase database, IEventPublisher eventPublisher, Action<IServiceCollection>? configureServices)
            {
                _database = database;
                ServiceProvider = BuildProvider(eventPublisher, configureServices);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() { }

            private IServiceProvider BuildProvider(IEventPublisher eventPublisher, Action<IServiceCollection>? configureServices)
            {
                var services = new ServiceCollection();
                var factory = new TestDbContextFactory(_database.Options);
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(factory);
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)));
                services.AddSingleton(eventPublisher);
                services.AddSingleton<IEventStore>(new PublisherEventStore(eventPublisher));
                services.AddScoped<InboxStore>();
                services.AddScoped<InboxSubscriptionStore>();
                services.AddScoped<IWorkflowRunStore>(sp => new WorkflowRunStore(
                    factory,
                    new NoopEventStore(),
                    new NullDispatchGrainFactory(),
                    NullLogger<WorkflowRunStore>.Instance,
                    new Mohist.Server.Infrastructure.BackgroundTaskLauncher(),
                    new DispatchSnapshotStore(
                        factory,
                        NullLogger<DispatchSnapshotStore>.Instance) as IDispatchSnapshotStore));
                services.AddScoped<IIssueStore>(sp => new IssueStore(factory, new NoopEventStore(), new NullDispatchGrainFactory(), NullLogger<IssueStore>.Instance, new Mohist.Server.Infrastructure.BackgroundTaskLauncher()));
                services.AddScoped<IStateStore<DomainIssue>>(sp => sp.GetRequiredService<IIssueStore>());
                configureServices?.Invoke(services);
                return services.BuildServiceProvider();
            }
        }
    }

    private sealed class PublisherEventStore : IEventStore
    {
        private readonly IEventPublisher _publisher;

        public PublisherEventStore(IEventPublisher publisher) => _publisher = publisher;

        public Task AppendAsync(CloudEvent evt, CancellationToken ct = default) =>
            _publisher.PublishAsync(evt, ct);

        public Task AppendAsync(MohistDbContext db, CloudEvent evt, CancellationToken ct = default) =>
            _publisher.PublishAsync(evt, ct);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
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
}
