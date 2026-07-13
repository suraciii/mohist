using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Logging;
using Mohist.Server.Otel;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Orleans.Configuration;
using Orleans.TestingHost;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.SpecTests.Support;

public class MohistIntegrationFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;
    private MohistWebApplicationFactory _factory = null!;
    private string? _runnerRoot;
    private string? _systemUpdateStatePath;
    private string? _logsPath;
    private readonly InMemoryLogFileStore _logFiles = new();
    // Allocates distinct silo/gateway ports per fixture so multiple integration
    // collections can run in parallel without fighting over 11111 / 30000.
    // Pattern from dotnet/orleans test/Orleans.Runtime.Tests/LocalhostSiloTests.cs.
    private TestClusterPortAllocator? _portAllocator;

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public IEventPublisher EventBus => _factory.Services.GetRequiredService<IEventPublisher>();
    public FakeGitService Git => _factory.Services.GetRequiredService<FakeGitService>();
    public FakeRunnerWorkspaceClient RunnerWorkspace => _factory.Services.GetRequiredService<FakeRunnerWorkspaceClient>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = null!;
    public string RunnerRoot => _runnerRoot ?? throw new InvalidOperationException("Fixture is not initialized");
    public string LogsPath => _logsPath ?? throw new InvalidOperationException("Fixture is not initialized");
    internal InMemoryLogFileStore LogFiles => _logFiles;

    public async Task UseDbAsync(Func<MohistDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await action(db);
    }

    public async Task<T> UseDbAsync<T>(Func<MohistDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await action(db);
    }

    public async Task InitializeAsync()
    {
        var dbName = $"mohist-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        await _keeper.OpenAsync();
        MigratedSqliteTemplate.CopyTo(_keeper);
        _runnerRoot = "/test/runner";
        _systemUpdateStatePath = "/test/system-update.json";
        _logsPath = "/test/logs";

        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);

        _factory = new MohistWebApplicationFactory(
            ConnectionString,
            _runnerRoot,
            _systemUpdateStatePath,
            _logsPath,
            TimeProvider,
            siloPort,
            gatewayPort,
            _logFiles);
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
        _portAllocator?.Dispose();
    }
}

public class MohistWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _runnerRoot;
    private readonly string _systemUpdateStatePath;
    private readonly string _logsPath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly string _configPath;
    private readonly string _artifactStorageRoot;
    private readonly int _siloPort;
    private readonly int _gatewayPort;
    private readonly InMemoryLogFileStore _logFiles;
    private readonly InMemoryHostFileDependencies _fileDependencies = new();
    // Keeper for the in-memory OtelDb override; disposed with the factory.
    private SqliteConnection? _otelKeeper;

    public string LogsPath => _logsPath;

    public MohistWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        FakeTimeProvider? timeProvider = null,
        int? siloPort = null,
        int? gatewayPort = null)
        : this(
            connectionString,
            runnerRoot,
            systemUpdateStatePath,
            "/test/logs",
            timeProvider,
            siloPort,
            gatewayPort)
    {
    }

    internal MohistWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        string logsPath,
        FakeTimeProvider? timeProvider = null,
        int? siloPort = null,
        int? gatewayPort = null,
        InMemoryLogFileStore? logFiles = null)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        _logsPath = logsPath;
        _timeProvider = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        _configPath = "/test/config.jsonc";
        _artifactStorageRoot = "/test/artifacts";
        _siloPort = siloPort ?? EndpointOptions.DEFAULT_SILO_PORT;
        _gatewayPort = gatewayPort ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
        _logFiles = logFiles ?? new InMemoryLogFileStore();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(MohistHostEnvironment.Testing);
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", _artifactStorageRoot);
        builder.UseSetting("Mohist:LogsPath", _logsPath);
        builder.UseSetting("Mohist:Silo:SiloPort", _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Mohist:Silo:GatewayPort", _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = _artifactStorageRoot,
                ["Mohist:LogsPath"] = _logsPath,
                ["Mohist:CliSkillDataPath"] = "/test/skill-data",
                ["Mohist:Silo:SiloPort"] = _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:Silo:GatewayPort"] = _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:AgentJob:DispatchBackoffInitial"] = "00:00:00.050",
                ["Mohist:AgentJob:DispatchBackoffCap"] = "00:00:00.200",
                ["Mohist:AgentJob:DispatchRetryBound"] = "00:00:05",
                ["Mohist:AgentJob:JobTimeout"] = "00:00:08",
                ["Mohist:Notifications:Hermes:WebhookUrl"] = null,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGitService>();
            services.AddSingleton<FakeGitService>();
            services.AddSingleton<IGitService>(provider => provider.GetRequiredService<FakeGitService>());
            services.RemoveAll<IRunnerWorkspaceClient>();
            services.AddSingleton<FakeRunnerWorkspaceClient>();
            services.AddSingleton<IRunnerWorkspaceClient>(provider => provider.GetRequiredService<FakeRunnerWorkspaceClient>());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_timeProvider);
            services.RemoveAll<IHubContext<RunnerHub>>();
            services.AddSingleton<RecordingRunnerHubContext>();
            services.AddSingleton<IHubContext<RunnerHub>>(provider => provider.GetRequiredService<RecordingRunnerHubContext>());
            services.RemoveAll<ILogFileStore>();
            services.AddSingleton<ILogFileStore>(_logFiles);
            services.RemoveAll<FileLoggerProvider>();
            services.RemoveAll<ILoggerProvider>();
            services.RemoveAll<IEnvironmentVariableProvider>();
            services.AddSingleton<IEnvironmentVariableProvider>(_ =>
            {
                var environment = new MockEnvironmentVariableProvider();
                environment[MohistWorkspaceLayout.RunnerRootEnvironmentVariable] = _runnerRoot;
                environment[SystemInfoService.HomeEnvironmentVariable] = "/test/home";
                return environment;
            });
            _fileDependencies.ReplaceServiceRegistrations(
                services,
                _configPath,
                _artifactStorageRoot,
                "/test/attachments",
                _timeProvider);

            services.RemoveAll<IDbContextFactory<MohistDbContext>>();
            services.AddDbContextFactory<MohistDbContext>(options =>
                options
                    .UseSqlite(_connectionString));
            // Replace the file-backed production OtelDb (which would otherwise
            // resolve to $HOME/.mohist/otel.db) with an in-memory instance so
            // the integration factory never creates a real otel.db file
            // (design/testing.md hard-constraint 1). The keeper connection is
            // owned by this factory and disposed in Dispose(bool).
            var (otelDb, otelKeeper) = InMemoryOtelDb.Create();
            _otelKeeper = otelKeeper;
            services.RemoveAll<OtelDb>();
            services.AddSingleton(otelDb);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _otelKeeper?.Dispose();
        }
        base.Dispose(disposing);
    }

}
