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
/// Test support for <see cref="RoutingDispatchHandler"/>. Each
/// test stands up an in-memory SQLite with the RoutingRules table
/// materialised (no full EF Migrate() needed — the dispatch handler only
/// reads RoutingRule rows + Agent rows + AgentJob launches,
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
        var logger = new RecordingLogger<RoutingDispatchHandler>();
        var scopeFactory = new TestScopeFactory(database, recording);
        var handler = new RoutingDispatchHandler(
            scopeFactory,
            logger);
        return (recording, new TestScope(database, handler, logger));
    }

    public static async Task SeedProjectAsync(TestScope scope, string projectId)
    {
        var database = scope.Database;
        await using var db = database.CreateContext();
        if (await db.Projects.AnyAsync(p => p.Id == projectId))
            return;
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId.Replace('_', '-'),
            RepositoriesJson = """[{"name":"test-repo","gitUrl":"git@example.com:test-repo.git","baseBranch":"main","isDefault":true}]""",
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
        await using var db = database.CreateContext();
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

    public static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateMigrated();

    public sealed class TestScope : IAsyncDisposable
    {
        private readonly TestSqliteDatabase _database;
        public TestScope(
            TestSqliteDatabase database,
            RoutingDispatchHandler handler,
            RecordingLogger<RoutingDispatchHandler> logger)
        {
            _database = database;
            Handler = handler;
            Logger = logger;
        }
        public TestSqliteDatabase Database => _database;
        public RoutingDispatchHandler Handler { get; }
        public RecordingLogger<RoutingDispatchHandler> Logger { get; }
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
        private readonly TestSqliteDatabase _database;
        private readonly RecordingAgentLauncher _launcher;
        public TestScopeFactory(TestSqliteDatabase database, RecordingAgentLauncher launcher)
        {
            _database = database;
            _launcher = launcher;
        }
        public IServiceScope CreateScope() => new TestScopeImpl(_database, _launcher);

        private sealed class TestScopeImpl : IServiceScope
        {
            public TestScopeImpl(TestSqliteDatabase database, RecordingAgentLauncher launcher)
            {
                var services = new ServiceCollection();
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(new TestDbContextFactory(database.Options));
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
