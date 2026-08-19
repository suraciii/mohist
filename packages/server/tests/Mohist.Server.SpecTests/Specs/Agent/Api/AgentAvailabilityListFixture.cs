using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

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
    private IReadOnlyList<RunnerStatusView> _onlineRunners;

    public CountingRunnerStatusSource(IReadOnlyList<RunnerStatusView> onlineRunners)
    {
        _onlineRunners = onlineRunners;
    }

    public IReadOnlyList<RunnerStatusView> OnlineRunners => _onlineRunners;
    public int CallCount { get; private set; }

    public void SetOnlineRunners(IReadOnlyList<RunnerStatusView> runners) => _onlineRunners = runners;

    public void Reset() => CallCount = 0;

    public Task<IReadOnlyList<RunnerStatusView>> GetOnlineRunnersAsync(string projectId, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_onlineRunners);
    }
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

    public HttpClient Client { get; private set; } = null!;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero));
    public CountingRunnerStatusSource RunnerStatus => _runnerStatus;
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
            _runnerStatus);
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        await _factory.EnsureSchemaAsync();
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
    }

    private sealed class AvailabilityWebApplicationFactory : MohistWebApplicationFactory
    {
        private readonly CountingRunnerStatusSource _runnerStatus;

        public AvailabilityWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            FakeTimeProvider timeProvider,
            CountingRunnerStatusSource runnerStatus)
            : base(connectionString, runnerRoot, systemUpdateStatePath, timeProvider)
        {
            _runnerStatus = runnerStatus;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRunnerStatusSource>();
                services.AddSingleton<IRunnerStatusSource>(_runnerStatus);
            });
        }
    }
}
