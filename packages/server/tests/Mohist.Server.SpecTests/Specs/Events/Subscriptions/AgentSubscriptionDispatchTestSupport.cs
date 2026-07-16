using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events.Subscriptions;

/// <summary>
/// Test support for <see cref="AgentSubscriptionDispatchHandler"/>. Each
/// test stands up an in-memory SQLite with the AgentSubscription table
/// materialised (no full EF Migrate() needed — the dispatch handler only
/// reads/writes AgentSubscription rows + Agent rows + AgentJob launches,
/// the latter going through the recording <see cref="RecordingAgentLauncher"/>
/// stub). Each <see cref="Build"/> call creates a fresh <see cref="RecordingAgentLauncher"/>
/// instance per test so the test can assert on captured launch calls.
/// </summary>
internal static class AgentSubscriptionDispatchTestSupport
{
    public static (RecordingAgentLauncher Launcher, TestScope Scope) Build()
    {
        var database = CreateDatabase();
        var recording = new RecordingAgentLauncher();
        var logger = new RecordingLogger<AgentSubscriptionDispatchHandler>();
        var scopeFactory = new TestScopeFactory(database, recording);
        var handler = new AgentSubscriptionDispatchHandler(
            scopeFactory,
            logger);
        return (recording, new TestScope(database, handler, logger));
    }

    public static async Task SeedProjectAsync(TestScope scope, string projectId)
    {
        var database = scope.Database;
        await using var db = database.CreateDbContext();
        if (await db.Projects.AnyAsync(p => p.Id == projectId))
            return;
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId.Replace('_', '-'),
            RepositoriesJson = "[]",
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedAgentAsync(
        TestScope scope,
        string projectId,
        string agentId,
        string agentName,
        string status = AgentStatus.Active,
        string instructions = "")
    {
        var database = scope.Database;
        await SeedProjectAsync(scope, projectId);

        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentName,
            Description = $"description for {agentName}",
            Instructions = instructions,
            AgentConfig = null,
            Skills = Array.Empty<string>(),
            MaxConcurrentRuns = null,
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        await using var db = database.CreateDbContext();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentName,
            Status = status,
            State = Mohist.Server.Infrastructure.JSON.Serialize(agent),
        });
        await db.SaveChangesAsync();
    }

    public static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
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

    public sealed class TestScope : IAsyncDisposable
    {
        private readonly TestDatabase _database;
        public TestScope(
            TestDatabase database,
            AgentSubscriptionDispatchHandler handler,
            RecordingLogger<AgentSubscriptionDispatchHandler> logger)
        {
            _database = database;
            Handler = handler;
            Logger = logger;
        }
        public TestDatabase Database => _database;
        public AgentSubscriptionDispatchHandler Handler { get; }
        public RecordingLogger<AgentSubscriptionDispatchHandler> Logger { get; }
        public async ValueTask DisposeAsync() => await _database.DisposeAsync();
    }

    public sealed record RecordedLog(LogLevel Level, string Message, Exception? Exception);

    public sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new RecordedLog(logLevel, formatter(state, exception), exception));

        private sealed class NoopScope : IDisposable
        {
            public static NoopScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Captures every <see cref="IAgentLauncher.LaunchAsync"/> call so the
    /// test can assert on what the dispatch handler triggered.
    /// </summary>
    public sealed class RecordingAgentLauncher : IAgentLauncher
    {
        public List<RecordedLaunch> Calls { get; } = new();
        public Exception? Failure { get; set; }

        public Task<AgentLaunchResult> LaunchAsync(
            AgentInfo agent,
            string prompt,
            AgentLaunchContext context,
            IReadOnlyDictionary<string, string>? triggerLabels = null,
            CancellationToken ct = default)
        {
            Calls.Add(new RecordedLaunch(agent, prompt, context, triggerLabels));
            if (Failure is not null)
                return Task.FromException<AgentLaunchResult>(Failure);
            return Task.FromResult(new AgentLaunchResult(
                SessionId: $"session_{Calls.Count:D3}",
                AgentId: agent.Id,
                AgentName: agent.Name));
        }
    }

    public sealed record RecordedLaunch(
        AgentInfo Agent,
        string Prompt,
        AgentLaunchContext Context,
        IReadOnlyDictionary<string, string>? TriggerLabels);

    private sealed class TestScopeFactory : IServiceScopeFactory
    {
        private readonly TestDatabase _database;
        private readonly RecordingAgentLauncher _launcher;
        public TestScopeFactory(TestDatabase database, RecordingAgentLauncher launcher)
        {
            _database = database;
            _launcher = launcher;
        }
        public IServiceScope CreateScope() => new TestScopeImpl(_database, _launcher);

        private sealed class TestScopeImpl : IServiceScope
        {
            public TestScopeImpl(TestDatabase database, RecordingAgentLauncher launcher)
            {
                var services = new ServiceCollection();
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(database.Factory);
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(TestTime.UtcNow));
                services.AddSingleton<IAgentLauncher>(launcher);
                services.AddScoped<AgentSubscriptionStore>();
                services.AddScoped<AgentQuerier>();
                ServiceProvider = services.BuildServiceProvider();
            }
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }
    }
}
