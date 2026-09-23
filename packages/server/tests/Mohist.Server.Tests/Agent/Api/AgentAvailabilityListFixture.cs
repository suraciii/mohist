using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

/// <summary>
/// Captures how many times the list-scoped
/// <c>GET /api/projects/{projectRef}/agents/availability</c> route asks
/// the runner-status source for online runners, and returns the runners
/// the test sets on it. The single-read acceptance criterion (issue
/// #133 / T-001) is asserted by reading <see cref="CallCount"/> after
/// the request: every fixture-level fake instance stays constant across
/// tests, so each test first calls <see cref="Reset"/> and then
/// <see cref="SetOnlineRunners"/> to install its own state.
/// </summary>
public sealed class CountingRunnerStatusSource : IRunnerStatusSource
{
    private RunnerStatusListSnapshot _snapshot;

    public CountingRunnerStatusSource(IReadOnlyList<RunnerStatusView> onlineRunners)
    {
        _snapshot = SnapshotFrom(onlineRunners);
    }

    public int CallCount { get; private set; }

    public void SetOnlineRunners(IReadOnlyList<RunnerStatusView> runners) =>
        _snapshot = SnapshotFrom(runners);

    public void Reset() => CallCount = 0;

    public Task<RunnerStatusListSnapshot> GetGlobalRunnersAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_snapshot);
    }

    private static RunnerStatusListSnapshot SnapshotFrom(IReadOnlyList<RunnerStatusView> runners)
    {
        var entries = runners
            .Select(runner => new RunnerStatusEntry(
                new RunnerIdentityStatusView(runner.Id, runner.Hostname, runner.Kind, null, null, null, null),
                new RunnerPresenceStatusView("online", runner.LastHeartbeatAt),
                new RunnerControlStatusView(runner.ConnectionState ?? "disconnected", null),
                new RunnerAdmissionStatusView("ready", []),
                runner.Capabilities,
                [],
                new RunnerStatusCapacityView(runner.Capacity?.UsedSlots, runner.Capacity?.TotalSlots ?? 0),
                runner.ActiveWorks,
                null,
                []))
            .ToList();
        return new RunnerStatusListSnapshot(DateTimeOffset.UnixEpoch, entries);
    }
}

/// <summary>
/// Counts derived capacity reads across a request so the availability
/// routes' single-batched-read guarantee is asserted at the wire boundary.
/// </summary>
public sealed class AgentCapacityReadCounter
{
    public int Count { get; set; }

    public void Reset() => Count = 0;
}

/// <summary>
/// Test decorator around the real <see cref="AgentCapacityStore"/> that
/// counts reads while delegating every call, so the counted path stays the
/// production SQLite projection.
/// </summary>
public sealed class CountingAgentCapacityStore : IAgentCapacityStore
{
    private readonly IAgentCapacityStore _inner;
    private readonly AgentCapacityReadCounter _counter;

    public CountingAgentCapacityStore(IAgentCapacityStore inner, AgentCapacityReadCounter counter)
    {
        _inner = inner;
        _counter = counter;
    }

    public Task<IReadOnlyDictionary<string, AgentCapacitySnapshot>> ReadAsync(
        string projectId,
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct = default)
    {
        _counter.Count++;
        return _inner.ReadAsync(projectId, agentIds, ct);
    }

    public Task<AgentJobCapacityClaimResult> ClaimJobAsync(
        string jobKey, long expectedRevision, CancellationToken ct = default) =>
        _inner.ClaimJobAsync(jobKey, expectedRevision, ct);

    public Task<AgentTurnCapacityClaimResult> ClaimTurnAsync(
        string sessionId, string expectedStateJson, string turnId, CancellationToken ct = default) =>
        _inner.ClaimTurnAsync(sessionId, expectedStateJson, turnId, ct);
}

/// <summary>
/// Test fixture backing <see cref="AgentAvailabilityListRoutesSpecs"/>.
/// Replaces the registered <see cref="IRunnerStatusSource"/> with the
/// counting fake so the route's single-read guarantee can be asserted
/// at the wire boundary. The concrete <see cref="RunnerStatusService"/>
/// stays in DI for every other route that depends on it.
/// </summary>
public sealed class AgentAvailabilityListFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;
    private AvailabilityWebApplicationFactory _factory = null!;
    private readonly CountingRunnerStatusSource _runnerStatus = new(Array.Empty<RunnerStatusView>());
    private readonly AgentCapacityReadCounter _capacityReads = new();

    public HttpClient Client { get; private set; } = null!;
    public FakeTimeProvider TimeProvider { get; } = new(TestTime.UtcNow);
    public CountingRunnerStatusSource RunnerStatus => _runnerStatus;
    public AgentCapacityReadCounter CapacityReads => _capacityReads;
    public IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        var dbName = $"agent-availability-list-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        await _keeper.OpenAsync();
        MigratedSqliteTemplate.CopyTo(_keeper);

        _factory = new AvailabilityWebApplicationFactory(
            connectionString,
            $"/mohist-tests/availability-list/runner-{dbName}",
            $"/mohist-tests/availability-list/system-update-{dbName}.json",
            TimeProvider,
            _runnerStatus,
            _capacityReads);
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        await _factory.EnsureSchemaAsync();
        using var health = await Client.GetAsync("/api/health");
        health.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    public void SetOnlineRunners(IReadOnlyList<RunnerStatusView> runners)
    {
        _runnerStatus.Reset();
        _runnerStatus.SetOnlineRunners(runners);
        _capacityReads.Reset();
    }

    private sealed class AvailabilityWebApplicationFactory : MohistWebApplicationFactory
    {
        private readonly CountingRunnerStatusSource _runnerStatus;
        private readonly AgentCapacityReadCounter _capacityReads;

        public AvailabilityWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            FakeTimeProvider timeProvider,
            CountingRunnerStatusSource runnerStatus,
            AgentCapacityReadCounter capacityReads)
            : base(connectionString, runnerRoot, systemUpdateStatePath, timeProvider)
        {
            _runnerStatus = runnerStatus;
            _capacityReads = capacityReads;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRunnerStatusSource>();
                services.AddSingleton<IRunnerStatusSource>(_runnerStatus);
                // The real derived store stays underneath; the decorator only
                // counts reads so the batched-read guarantee is observable.
                services.AddSingleton(_capacityReads);
                services.RemoveAll<IAgentCapacityStore>();
                services.AddScoped<AgentCapacityStore>();
                services.AddScoped<IAgentCapacityStore>(provider => new CountingAgentCapacityStore(
                    provider.GetRequiredService<AgentCapacityStore>(),
                    provider.GetRequiredService<AgentCapacityReadCounter>()));
            });
        }
    }
}
