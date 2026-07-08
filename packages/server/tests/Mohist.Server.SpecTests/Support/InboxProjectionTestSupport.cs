using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Domain.Run;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Shared in-memory SQLite + service-scope harness for unit specs that
/// exercise <see cref="InboxProjectionHandler"/>. Each test stands up a
/// fresh in-memory database, seeds the issue / workflow-run rows it
/// needs, drives the handler with one CloudEvent envelope, and inspects
/// the resulting inbox row.
/// </summary>
internal static class InboxProjectionTestSupport
{
    public static InboxProjectionHandler CreateHandler(TestDatabase database) =>
        CreateHandler(database, new NoopEventPublisher());

    public static InboxProjectionHandler CreateHandler(TestDatabase database, IEventPublisher eventPublisher) =>
        CreateHandler(database, eventPublisher, configureServices: null);

    public static InboxProjectionHandler CreateHandler(
        TestDatabase database,
        IEventPublisher eventPublisher,
        Action<IServiceCollection>? configureServices) =>
        new(
            scopeFactory: new InboxScopeFactory(database, eventPublisher, configureServices),
            log: NullLogger<InboxProjectionHandler>.Instance);

    public static CloudEvent BuildIssueEvent(string type, string projectId, string issueId, int issueNumber, string eventId) =>
        new(
            id: eventId,
            source: new Uri($"/mohist/issues/{issueId}", UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = issueNumber.ToString(),
            });

    public static CloudEvent BuildWorkflowEvent(string type, string workflowRunId, string eventId) =>
        new(
            id: eventId,
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: null);

    public static async Task<List<InboxItemView>> GetInboxAsync(TestDatabase database, string projectId)
    {
        await using var db = database.CreateDbContext();
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
                IssueId = r.IssueId,
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
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        string title)
    {
        await SeedProjectAsync(database, projectId);

        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = title,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
        };
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedProjectAsync(TestDatabase database, string projectId)
    {
        await using var db = database.CreateDbContext();
        if (await db.Projects.AnyAsync(p => p.Id == projectId))
            return;

        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = ProjectNameFromId(projectId),
            RepositoriesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedSubscriptionAsync(
        TestDatabase database,
        string projectId,
        bool workflowFailedEnabled = true,
        bool approvalRequestedEnabled = true,
        bool issueStartedEnabled = true,
        bool issueCompletedEnabled = true)
    {
        await SeedProjectAsync(database, projectId);

        var store = new InboxSubscriptionStore(
            database.Factory,
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
        TestDatabase database,
        string workflowRunId,
        string? projectId,
        string? issueId,
        int? issueNumber)
    {
        var run = BuildWorkflowRun(workflowRunId, projectId, issueId, issueNumber);
        var json = Mohist.Server.Infrastructure.JSON.Serialize(run);
        await using var db = database.CreateDbContext();
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
        string? issueId,
        int? issueNumber)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectId is not null) annotations["projectId"] = projectId;
        if (issueId is not null) annotations["issueId"] = issueId;
        if (issueNumber is not null) annotations["issueNumber"] = issueNumber.Value.ToString();

        return new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: annotations),
            Stages = new List<StageRun>(),
        };
    }

    public static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }

    public static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    public sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    public sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    public sealed class NoopEventStore : IEventStore
    {
        public Task AppendAsync(CloudEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task AppendAsync(MohistDbContext db, CloudEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>(Array.Empty<StoredCloudEvent>());
        public Task MarkDispatchedAsync(string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;
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
        private readonly TestDatabase _database;
        private readonly IEventPublisher _eventPublisher;
        private readonly Action<IServiceCollection>? _configureServices;

        public InboxScopeFactory(
            TestDatabase database,
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
            private readonly TestDatabase _database;
            public InboxScope(TestDatabase database, IEventPublisher eventPublisher, Action<IServiceCollection>? configureServices)
            {
                _database = database;
                ServiceProvider = BuildProvider(eventPublisher, configureServices);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() { }

            private IServiceProvider BuildProvider(IEventPublisher eventPublisher, Action<IServiceCollection>? configureServices)
            {
                var services = new ServiceCollection();
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(_database.Factory);
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)));
                services.AddSingleton(eventPublisher);
                services.AddScoped<InboxStore>();
                services.AddScoped<InboxSubscriptionStore>();
                services.AddScoped<IWorkflowRunStore>(sp => new WorkflowRunStore(
                    _database.Factory,
                    new NoopEventStore()));
                services.AddScoped<IIssueStore>(sp => new IssueStore(_database.Factory, new NoopEventStore()));
                services.AddScoped<IStateStore<DomainIssue>>(sp => sp.GetRequiredService<IIssueStore>());
                configureServices?.Invoke(services);
                return services.BuildServiceProvider();
            }
        }
    }
}
