using System.Net;
using Microsoft.AspNetCore.Builder;
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
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Workspace;
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
    public const string OperatorToken = "test-operator-token-0123456789abcdef";
    private SqliteConnection _keeper = null!;
    private MohistWebApplicationFactory _factory = null!;
    private string? _runnerRoot;
    private string? _systemUpdateStatePath;
    private string? _logsPath;
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
    public AgentJobDispatchProbe AgentJobDispatches => _factory.Services.GetRequiredService<AgentJobDispatchProbe>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = null!;
    public string RunnerRoot => _runnerRoot ?? throw new InvalidOperationException("Fixture is not initialized");
    public string LogsPath => _logsPath ?? throw new InvalidOperationException("Fixture is not initialized");

    public async Task InitializeAsync()
    {
        var dbName = $"mohist-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        await _keeper.OpenAsync();
        _runnerRoot = Path.Combine(Path.GetTempPath(), $"mohist-runner-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runnerRoot);
        _systemUpdateStatePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        _logsPath = Path.Combine(Path.GetTempPath(), $"mohist-logs-{Guid.NewGuid():N}");

        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);

        _factory = new MohistWebApplicationFactory(ConnectionString, _runnerRoot, _systemUpdateStatePath, _logsPath, TimeProvider, siloPort, gatewayPort);
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add(Mohist.Server.Infrastructure.Security.OperatorCredential.HeaderName, OperatorToken);
        await _factory.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
        if (!string.IsNullOrWhiteSpace(_runnerRoot) && Directory.Exists(_runnerRoot))
            Directory.Delete(_runnerRoot, recursive: true);
        if (!string.IsNullOrWhiteSpace(_systemUpdateStatePath) && File.Exists(_systemUpdateStatePath))
            File.Delete(_systemUpdateStatePath);
        if (!string.IsNullOrWhiteSpace(_factory?.ArtifactStorageRoot) && Directory.Exists(_factory.ArtifactStorageRoot))
            Directory.Delete(_factory.ArtifactStorageRoot, recursive: true);
        if (!string.IsNullOrWhiteSpace(_logsPath) && Directory.Exists(_logsPath))
            Directory.Delete(_logsPath, recursive: true);
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
    private string? _webRoot;
    // Keeper for the in-memory OtelDb override; disposed with the factory.
    private SqliteConnection? _otelKeeper;

    public string ArtifactStorageRoot => _artifactStorageRoot;
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
            Path.Combine(Path.GetTempPath(), $"mohist-logs-{Guid.NewGuid():N}"),
            timeProvider,
            siloPort,
            gatewayPort)
    {
    }

    public MohistWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        string logsPath,
        FakeTimeProvider? timeProvider = null,
        int? siloPort = null,
        int? gatewayPort = null)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        _logsPath = logsPath;
        _timeProvider = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        _configPath = Path.Combine(Path.GetTempPath(), $"mohist-config-{Guid.NewGuid():N}.jsonc");
        _artifactStorageRoot = Path.Combine(Path.GetTempPath(), $"mohist-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactStorageRoot);
        _siloPort = siloPort ?? EndpointOptions.DEFAULT_SILO_PORT;
        _gatewayPort = gatewayPort ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(MohistHostEnvironment.Testing);
        _webRoot ??= CreateWebRoot();
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:WebRoot", _webRoot);
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
                ["Mohist:WebRoot"] = _webRoot,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = _artifactStorageRoot,
                ["Mohist:LogsPath"] = _logsPath,
                ["Mohist:Silo:SiloPort"] = _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:Silo:GatewayPort"] = _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:AgentJob:DispatchBackoffInitial"] = "00:00:00.050",
                ["Mohist:AgentJob:DispatchBackoffCap"] = "00:00:00.200",
                ["Mohist:AgentJob:DispatchRetryBound"] = "00:00:05",
                ["Mohist:AgentJob:JobTimeout"] = "00:00:08",
                ["Mohist:Notifications:Hermes:WebhookUrl"] = null,
                ["Mohist:OperatorToken"] = MohistIntegrationFixture.OperatorToken,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGitService>();
            services.AddSingleton<IStartupFilter, LoopbackTestConnectionStartupFilter>();
            services.AddSingleton<FakeGitService>();
            services.AddSingleton<IGitService>(provider => provider.GetRequiredService<FakeGitService>());
            services.RemoveAll<IRunnerWorkspaceClient>();
            services.AddSingleton<FakeRunnerWorkspaceClient>();
            services.AddSingleton<IRunnerWorkspaceClient>(provider => provider.GetRequiredService<FakeRunnerWorkspaceClient>());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_timeProvider);
            services.RemoveAll<IAgentJobDispatchObserver>();
            services.AddSingleton<AgentJobDispatchProbe>();
            services.AddSingleton<IAgentJobDispatchObserver>(provider => provider.GetRequiredService<AgentJobDispatchProbe>());
            services.RemoveAll<IHubContext<RunnerHub>>();
            services.AddSingleton<RecordingRunnerHubContext>();
            services.AddSingleton<IHubContext<RunnerHub>>(provider => provider.GetRequiredService<RecordingRunnerHubContext>());
            services.RemoveAll<ConfigService>();
            services.RemoveAll<IEnvironmentVariableProvider>();
            services.AddSingleton<IEnvironmentVariableProvider>(_ =>
            {
                var environment = new MockEnvironmentVariableProvider();
                environment[MohistWorkspaceLayout.RunnerRootEnvironmentVariable] = _runnerRoot;
                return environment;
            });
            services.AddSingleton(provider => new ConfigService(
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<IEnvironmentVariableProvider>(),
                provider.GetRequiredService<ILogger<ConfigService>>(),
                _configPath));

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

    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Attachments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Attachments" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "OwnerKind" TEXT NULL,
                "OwnerId" TEXT NULL,
                "OriginalFileName" TEXT NOT NULL,
                "ContentType" TEXT NULL,
                "Size" INTEGER NOT NULL,
                "StoragePath" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Attachments_ExpiresAt\" ON \"Attachments\" (\"ExpiresAt\");");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Attachments_ProjectId_Owner\" ON \"Attachments\" (\"ProjectId\", \"OwnerKind\", \"OwnerId\");");

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "LabelDefinitions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LabelDefinitions" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Key" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "SupportedValuesJson" TEXT NOT NULL DEFAULT '[]',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_LabelDefinitions_ProjectId_Key\" ON \"LabelDefinitions\" (\"ProjectId\", \"Key\");");

        // Issue-318 T-002: Program.cs already calls db.Database.Migrate()
        // during host startup, but the migration that materializes the
        // WorkflowRuns STORED status computed column is produced in T-004.
        // Apply the test-only schema fixup so the new status-filter
        // queries (FindAssignableAsync / FindAssignedToAsync /
        // CountRunningAssignedToAsync) have the column and index they
        // expect. Idempotent — safe to call before/after Migrate().
        GrainTestConfig.ApplyWorkflowRunsStatusSchemaFix(db);
    }

    private static string CreateWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html><body>Mohist Test Web</body></html>");
        return root;
    }
}

public sealed class LoopbackTestConnectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
            context.Connection.LocalIpAddress ??= IPAddress.Loopback;
            await continuation();
        });
        next(app);
    };
}

public sealed class RecordingRunnerHubContext : IHubContext<RunnerHub>
{
    private readonly RecordingHubClients _clients;
    private readonly Dictionary<string, object?> _invocationResponses = new(StringComparer.Ordinal);

    public RecordingRunnerHubContext()
    {
        _clients = new RecordingHubClients(this);
    }

    public List<RecordedRunnerHubMessage> SentMessages { get; } = [];
    public List<RecordedRunnerHubInvocation> Invocations { get; } = [];
    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new NoopGroupManager();

    public void Clear()
    {
        SentMessages.Clear();
        Invocations.Clear();
    }

    /// <summary>
    /// Registers a return value the recording proxy should hand back when a
    /// server-side invocation targets the named method on any connection
    /// (issue-129 T-005). Only the most recent registration for a method
    /// wins, so a test can overwrite a prior response between assertions.
    /// </summary>
    public void SetInvocationResponse(string method, object? response)
    {
        _invocationResponses[method] = response;
    }

    private object? ResolveInvocationResponse(string method)
    {
        return _invocationResponses.TryGetValue(method, out var value) ? value : null;
    }

    private sealed class RecordingHubClients : IHubClients
    {
        private readonly RecordingRunnerHubContext _context;

        public RecordingHubClients(RecordingRunnerHubContext context)
        {
            _context = context;
        }

        public IClientProxy All => new RecordingClientProxy(_context, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, "all-except");
        // IHubClients<T> declares `Client(string) -> T` (IClientProxy here);
        // the non-generic IHubClients inherits from IHubClients<IClientProxy>
        // and re-declares `Client(string) -> ISingleClientProxy` with a default
        // implementation that wraps the IClientProxy in a
        // NonInvokingSingleClientProxy (which throws NotImplementedException
        // for InvokeCoreAsync<T>). Implement both overloads explicitly so
        // callers using the non-generic IHubClients.Client(connectionId) also
        // get an ISingleClientProxy that records invocations (issue-129
        // T-005 CancelAgentSession). The recording proxy implements both
        // IClientProxy and ISingleClientProxy so the wire semantics are
        // unchanged for SendCoreAsync.
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        ISingleClientProxy IHubClients.Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy(_context, string.Join(",", connectionIds));
        public IClientProxy Group(string groupName) => new RecordingClientProxy(_context, groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy(_context, string.Join(",", groupNames));
        public IClientProxy User(string userId) => new RecordingClientProxy(_context, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy(_context, string.Join(",", userIds));
    }

    private sealed class RecordingClientProxy : ISingleClientProxy
    {
        private readonly RecordingRunnerHubContext _context;
        private readonly string _connectionId;

        public RecordingClientProxy(RecordingRunnerHubContext context, string connectionId)
        {
            _context = context;
            _connectionId = connectionId;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _context.SentMessages.Add(new RecordedRunnerHubMessage(_connectionId, method, args));
            return Task.CompletedTask;
        }

        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _context.Invocations.Add(new RecordedRunnerHubInvocation(_connectionId, method, args));
            var response = _context.ResolveInvocationResponse(method);
            if (response is T typed)
            {
                return Task.FromResult(typed);
            }
            // Fall back to default(T) when no response is registered or the
            // registered response is the wrong runtime type. Tests that
            // exercise the typed return path must set the response to the
            // exact T via SetInvocationResponse before invoking the route.
            return Task.FromResult(default(T)!);
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed record RecordedRunnerHubMessage(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);

public sealed record RecordedRunnerHubInvocation(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);
