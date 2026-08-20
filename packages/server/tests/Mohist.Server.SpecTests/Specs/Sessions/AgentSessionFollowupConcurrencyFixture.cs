using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans.Configuration;
using Orleans.Reminders;
using Orleans.TestingHost;
using Xunit;
using AgentDomain = Mohist.Server.Agent.Domain.Agent;
using AgentStatusDomain = Mohist.Server.Agent.Domain.AgentStatus;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Self-contained InProcessTestCluster fixture that wires the
/// AgentSessionGrain alongside the AgentConcurrencyGrain (the
/// per-agent permit authority introduced by issue-520 T-001) so
/// T-002's follow-up gate can be exercised without depending on the
/// full HTTP integration stack. Agent rows are seeded directly into
/// the SQLite-backed MohistDbContext that AgentQuerier reads from;
/// AgentConcurrencyGrain stores permit state in the default
/// in-memory grain storage.
/// </summary>
public sealed class AgentSessionFollowupConcurrencyFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public InMemoryConcurrencyFakeStore StateStore { get; } = new();
    public InMemoryConcurrencyTranscriptStore TranscriptStore { get; } = new();
    public RecordingPublisher TranscriptPublisher { get; } = new();
    public AgentSessionPersistenceTestProbe Persistence { get; }
    public TestLogger<AgentSessionGrain> Logger { get; } = new();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public IDbContextFactory<MohistDbContext> DbFactory => _database is null
        ? throw new InvalidOperationException("fixture not initialised")
        : new TestSqliteDatabaseContextFactory(_database);

    public MohistDbContext CreateDbContext() => _database!.CreateContext();

    public async Task SeedAgentAsync(string projectId, string agentId, int? maxConcurrentRuns)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var agent = new AgentDomain
        {
            Id = agentId,
            ProjectId = projectId,
            Name = $"agent-{agentId}",
            Description = "spec",
            Instructions = "spec",
            Skills = Array.Empty<string>(),
            MaxConcurrentRuns = maxConcurrentRuns,
            Status = AgentStatusDomain.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var rowId = GrainKey.Agent(projectId, agentId);
        await using var db = await DbFactory.CreateDbContextAsync();
        var existing = await db.Agents.FindAsync(rowId);
        if (existing is null)
        {
            db.Agents.Add(new AgentRow
            {
                Id = rowId,
                ProjectId = projectId,
                Name = agent.Name,
                Status = agent.Status,
                State = AgentStore.Serialize(agent),
            });
        }
        else
        {
            existing.ProjectId = projectId;
            existing.Name = agent.Name;
            existing.Status = agent.Status;
            existing.State = AgentStore.Serialize(agent);
        }
        await db.SaveChangesAsync();
    }

    public void Reset()
    {
        StateStore.Reset();
        TranscriptStore.Reset();
        TranscriptPublisher.Clear();
        Logger.Entries.Clear();
    }

    private TestSqliteDatabase _database = null!;

    public AgentSessionFollowupConcurrencyFixture()
    {
        Persistence = new AgentSessionPersistenceTestProbe(
            () => TimeProvider.Advance(TimeSpan.FromSeconds(1)));
    }

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();

        var builder = new InProcessTestClusterBuilder().UseLogicalPorts();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.Configure<GrainCollectionOptions>(options => options.CollectionAge = TimeSpan.FromMinutes(10));
            // AgentConcurrencyGrain's reconciliation reminder (issue-520 T-001
            // D3) uses a 30s period; the in-memory reminder service still
            // enforces MinimumReminderPeriod by default, so lower the floor
            // for the test silo as the production silo does.
            siloBuilder.Configure<ReminderOptions>(options =>
                options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100));
            siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(_database.ConnectionString));

            siloBuilder.Services.AddSingleton<IAgentSessionStore>(StateStore);
            siloBuilder.Services.AddSingleton<IAgentSessionTranscriptStore>(TranscriptStore);
            siloBuilder.Services.AddSingleton<ITranscriptEventPublisher>(TranscriptPublisher);
            siloBuilder.Services.AddSingleton<IAgentSessionPersistenceObserver>(Persistence);
            siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider);
            siloBuilder.Services.AddSingleton<RunnerConnectionTracker>();
            siloBuilder.Services.AddSingleton<IAgentSessionConnectionRegistry>(sp =>
                sp.GetRequiredService<RunnerConnectionTracker>());
            siloBuilder.Services.AddSingleton<ILogger<AgentSessionGrain>>(Logger);
            siloBuilder.Services.AddSingleton<IEventStore>(new NoopEventStore());
            siloBuilder.Services.AddSingleton<IBackgroundTaskLauncher, BackgroundTaskLauncher>();
            siloBuilder.Services.AddScoped<AgentQuerier>();
            siloBuilder.Services.AddScoped<AgentJobQuerier>();
        });
        Cluster = builder.Build();
        return new ValueTask(Cluster.DeployAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Cluster.DisposeAsync();
        await _database.DisposeAsync();
    }

    public async Task<IAgentSessionGrain> OpenGenericAgentSessionAsync(
        string projectId,
        string agentId,
        string runnerId = "spec-runner",
        string workDir = "/tmp/spec-work")
    {
        var setup = await OpenGenericAgentSessionWithRuntimeIdAsync(projectId, agentId, runnerId, workDir);
        return setup.Grain;
    }

    public async Task<OpenedAgentSession> OpenGenericAgentSessionWithRuntimeIdAsync(
        string projectId,
        string agentId,
        string runnerId = "spec-runner",
        string workDir = "/tmp/spec-work")
    {
        var sessionId = $"followup-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var metadata = GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
            projectId,
            agentId,
            $"agent-{agentId}"));
        var grain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            WorkDir: workDir,
            Metadata: metadata));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            WorkDir: workDir));
        return new OpenedAgentSession(grain, runtimeSessionId);
    }

    public sealed record OpenedAgentSession(IAgentSessionGrain Grain, string RuntimeSessionId);

    public sealed class RecordingPublisher : ITranscriptEventPublisher
    {
        public List<TranscriptEnvelope> Published { get; } = [];

        public void Clear() => Published.Clear();

        public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default)
        {
            Published.Add(envelope);
            return Task.CompletedTask;
        }
    }

    public sealed class InMemoryConcurrencyFakeStore : IAgentSessionStore
    {
        private readonly Dictionary<string, AgentSession> _states = new(StringComparer.Ordinal);
        private string? _lastSavedKey;

        public int SaveCount { get; private set; }

        public void Reset()
        {
            _states.Clear();
            _lastSavedKey = null;
            SaveCount = 0;
        }

        public bool Contains(string key) => _states.ContainsKey(key);

        public AgentSession? State =>
            _lastSavedKey is not null && _states.TryGetValue(_lastSavedKey, out var state) ? state : null;

        public Task<AgentSession?> LoadAsync(string key) =>
            Task.FromResult(_states.TryGetValue(key, out var state) ? Clone(state) : null);

        public Task<IReadOnlyList<AgentSession>> ListAsync() =>
            Task.FromResult<IReadOnlyList<AgentSession>>(_states.Values.Select(Clone).ToArray());

        public Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(
            string runnerId,
            CancellationToken ct = default)
        {
            var matches = new List<AgentSessionReconcileBinding>();
            foreach (var state in _states.Values)
            {
                if (!string.Equals(state.Runtime.RunnerId, runnerId, StringComparison.Ordinal)
                    || state.Status.Activity == AgentSessionActivity.Idle
                    || string.IsNullOrWhiteSpace(state.Runtime.Runtime)
                    || string.IsNullOrWhiteSpace(state.Status.AgentRuntimeSessionId)
                    || string.IsNullOrWhiteSpace(state.Runtime.WorkDir))
                    continue;

                matches.Add(new AgentSessionReconcileBinding(
                    state.Id,
                    state.Runtime.Runtime!,
                    state.Status.AgentRuntimeSessionId,
                    state.Runtime.WorkDir));
            }

            return Task.FromResult<IReadOnlyList<AgentSessionReconcileBinding>>(matches);
        }

        public Task SaveAsync(string key, AgentSession state)
        {
            SaveCount++;
            _states[key] = Clone(state);
            _lastSavedKey = key;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
        {
            SaveCount++;
            _states[key] = Clone(state);
            _lastSavedKey = key;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            _states.Remove(key);
            if (string.Equals(_lastSavedKey, key, StringComparison.Ordinal))
                _lastSavedKey = null;
            return Task.CompletedTask;
        }

        private static AgentSession Clone(AgentSession state) =>
            JSON.Deserialize<AgentSession>(JSON.Serialize(state))
            ?? throw new InvalidOperationException("Failed to clone AgentSession state.");
    }

    public sealed class InMemoryConcurrencyTranscriptStore : IAgentSessionTranscriptStore
    {
        public void Reset()
        {
        }

        public Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

internal sealed class TestSqliteDatabaseContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly TestSqliteDatabase _database;

    public TestSqliteDatabaseContextFactory(TestSqliteDatabase database) => _database = database;

    public MohistDbContext CreateDbContext() => _database.CreateContext();

    public Task<MohistDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        Task.FromResult(_database.CreateContext());
}
