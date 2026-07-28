using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Api;
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
using Mohist.Server.Logging;
using Mohist.Server.Otel;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Services.Prompts;
using Orleans.Configuration;
using Orleans.TestingHost;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.SpecTests.Support;

public class MohistIntegrationFixture : IAsyncLifetime
{
    public const string OperatorToken = "test-operator-token-0123456789abcdef";
    private const string VirtualRunnerRoot = "/mohist-tests/runner";
    private const string VirtualSystemUpdateStatePath = "/mohist-tests/system-update.json";
    private const string VirtualLogsPath = "/mohist-tests/logs";
    private SqliteConnection _keeper = null!;
    private MohistWebApplicationFactory _factory = null!;
    private readonly bool _otelEnabled;
    // Allocates distinct silo/gateway ports per fixture so multiple integration
    // collections can run in parallel without fighting over 11111 / 30000.
    // Pattern from dotnet/orleans test/Orleans.Runtime.Tests/LocalhostSiloTests.cs.
    private TestClusterPortAllocator? _portAllocator;

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public FakeRunnerWorkspaceClient RunnerWorkspace => _factory.Services.GetRequiredService<FakeRunnerWorkspaceClient>();
    public AgentJobDispatchProbe AgentJobDispatches => _factory.Services.GetRequiredService<AgentJobDispatchProbe>();
    public AgentSessionPersistenceTestProbe Persistence => _factory.Persistence;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = null!;
    public string RunnerRoot => VirtualRunnerRoot;

    public MohistIntegrationFixture()
        : this(otelEnabled: false)
    {
    }

    protected MohistIntegrationFixture(bool otelEnabled)
    {
        _otelEnabled = otelEnabled;
    }

    public async ValueTask InitializeAsync()
    {
        var dbName = $"mohist-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        await _keeper.OpenAsync();

        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);

        _factory = new MohistWebApplicationFactory(
            ConnectionString,
            VirtualRunnerRoot,
            VirtualSystemUpdateStatePath,
            VirtualLogsPath,
            TimeProvider,
            siloPort,
            gatewayPort,
            _otelEnabled);
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add(Mohist.Server.Infrastructure.Security.OperatorCredential.HeaderName, OperatorToken);
        await _factory.EnsureSchemaAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
        _portAllocator?.Dispose();
    }
}

public sealed class OtelIntegrationFixture : MohistIntegrationFixture
{
    public OtelIntegrationFixture()
        : base(otelEnabled: true)
    {
    }
}

public class MohistWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _runnerRoot;
    private readonly string _systemUpdateStatePath;
    private readonly string _logsPath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly int _siloPort;
    private readonly int _gatewayPort;
    private readonly bool _otelEnabled;
    public AgentSessionPersistenceTestProbe Persistence { get; }
    // Keeper for the in-memory OtelDb override; disposed with the factory.
    private SqliteConnection? _otelKeeper;

    public string ArtifactStorageRoot => "/mohist-tests/artifacts";
    public string LogsPath => _logsPath;

    public MohistWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        FakeTimeProvider? timeProvider = null,
        int? siloPort = null,
        int? gatewayPort = null,
        bool otelEnabled = false)
        : this(
            connectionString,
            runnerRoot,
            systemUpdateStatePath,
            "/mohist-tests/logs",
            timeProvider,
            siloPort,
            gatewayPort,
            otelEnabled)
    {
    }

    public MohistWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        string logsPath,
        FakeTimeProvider? timeProvider = null,
        int? siloPort = null,
        int? gatewayPort = null,
        bool otelEnabled = false)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        _logsPath = logsPath;
        _timeProvider = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        _siloPort = siloPort ?? EndpointOptions.DEFAULT_SILO_PORT;
        _gatewayPort = gatewayPort ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
        _otelEnabled = otelEnabled;
        Persistence = new AgentSessionPersistenceTestProbe(
            () => _timeProvider.Advance(TimeSpan.FromSeconds(1)));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(MohistHostEnvironment.Testing);
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", ArtifactStorageRoot);
        builder.UseSetting("Mohist:LogsPath", _logsPath);
        builder.UseSetting("Mohist:Otel:Enabled", _otelEnabled ? "true" : "false");
        builder.UseSetting("Mohist:Silo:SiloPort", _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Mohist:Silo:GatewayPort", _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentSessionPersistenceObserver>();
            services.AddSingleton<IAgentSessionPersistenceObserver>(Persistence);
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = ArtifactStorageRoot,
                ["Mohist:LogsPath"] = _logsPath,
                ["Mohist:Otel:Enabled"] = _otelEnabled ? "true" : "false",
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
            for (var index = services.Count - 1; index >= 0; index--)
            {
                if (services[index].ServiceType == typeof(ILoggerProvider))
                    services.RemoveAt(index);
            }
            services.AddSingleton<ILoggerProvider, InMemoryLoggerProvider>();
            services.RemoveAll<ILogTailSource>();
            services.AddSingleton<InMemoryLogTailSource>();
            services.AddSingleton<ILogTailSource>(provider => provider.GetRequiredService<InMemoryLogTailSource>());
            services.RemoveAll<IEventTailSource>();
            services.AddSingleton<InMemoryEventTailSource>();
            services.AddSingleton<IEventTailSource>(provider => provider.GetRequiredService<InMemoryEventTailSource>());
            services.RemoveAll<Mohist.Server.SystemInfo.IFileSystem>();
            services.AddSingleton<Mohist.Server.SystemInfo.IFileSystem, InMemoryServerFileSystem>();
            services.RemoveAll<ISystemUpdateStore>();
            services.AddSingleton<InMemorySystemUpdateStore>();
            services.AddSingleton<ISystemUpdateStore>(provider => provider.GetRequiredService<InMemorySystemUpdateStore>());
            services.RemoveAll<IManagedAssetCatalog>();
            services.AddSingleton<IManagedAssetCatalog, InMemoryManagedAssetCatalog>();
            services.RemoveAll<IAttachmentStorage>();
            services.AddSingleton<InMemoryAttachmentStorage>();
            services.AddSingleton<IAttachmentStorage>(provider => provider.GetRequiredService<InMemoryAttachmentStorage>());
            services.RemoveAll<IWorkflowArtifactStorage>();
            services.AddSingleton<InMemoryWorkflowArtifactStorage>();
            services.AddSingleton<IWorkflowArtifactStorage>(provider => provider.GetRequiredService<InMemoryWorkflowArtifactStorage>());
            services.RemoveAll<IWebContentProvider>();
            services.AddSingleton<IWebContentProvider, InMemoryWebContentProvider>();
            services.RemoveAll<IPromptLoader>();
            services.AddSingleton<IPromptLoader>(_ => new InMemoryPromptLoader());
            services.AddSingleton<IStartupFilter, LoopbackTestConnectionStartupFilter>();
            services.RemoveAll<IRunnerWorkspaceClient>();
            services.AddSingleton<FakeRunnerWorkspaceClient>();
            services.AddSingleton<IRunnerWorkspaceClient>(provider => provider.GetRequiredService<FakeRunnerWorkspaceClient>());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_timeProvider);
            services.RemoveAll<IAgentJobDispatchObserver>();
            services.AddSingleton<AgentJobDispatchProbe>();
            services.AddSingleton<IAgentJobDispatchObserver>(provider => provider.GetRequiredService<AgentJobDispatchProbe>());
            services.RemoveAll<IAgentSessionPersistenceObserver>();
            services.AddSingleton<IAgentSessionPersistenceObserver>(Persistence);
            services.RemoveAll<IHubContext<RunnerHub>>();
            services.AddSingleton<RecordingRunnerHubContext>();
            services.AddSingleton<IHubContext<RunnerHub>>(provider => provider.GetRequiredService<RecordingRunnerHubContext>());
            services.RemoveAll<ConfigService>();
            services.RemoveAll<IConfigDocumentStore>();
            services.AddSingleton<InMemoryConfigDocumentStore>();
            services.AddSingleton<IConfigDocumentStore>(provider => provider.GetRequiredService<InMemoryConfigDocumentStore>());
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
                provider.GetRequiredService<IConfigDocumentStore>()));

            services.RemoveAll<IDbContextFactory<MohistDbContext>>();
            services.AddDbContextFactory<MohistDbContext>(options =>
                options
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new RequestWorkDbCommandInterceptor()));
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
    private readonly Dictionary<string, Func<IReadOnlyList<object?>, object?>> _invocationResponseFactories = new(StringComparer.Ordinal);

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
        _invocationResponses.Clear();
        _invocationResponseFactories.Clear();
    }

    /// <summary>
    /// Registers a return value the recording proxy should hand back when a
    /// server-side invocation targets the named method on any connection
    /// (issue-129 T-005). Only the most recent registration for a method
    /// wins, so a test can overwrite a prior response between assertions.
    /// </summary>
    public void SetInvocationResponse(string method, object? response)
    {
        _invocationResponseFactories.Remove(method);
        _invocationResponses[method] = response;
    }

    public void SetInvocationResponseFactory(
        string method,
        Func<IReadOnlyList<object?>, object?> responseFactory)
    {
        _invocationResponses.Remove(method);
        _invocationResponseFactories[method] = responseFactory;
    }

    private object? ResolveInvocationResponse(string method, IReadOnlyList<object?> arguments)
    {
        if (_invocationResponseFactories.TryGetValue(method, out var responseFactory))
            return responseFactory(arguments);
        if (string.Equals(method, "ReceiveFollowup", StringComparison.Ordinal)
            && !_invocationResponses.ContainsKey(method))
        {
            return new RunnerFollowupDeliveryResult(true);
        }
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
            _context.SentMessages.Add(new RecordedRunnerHubMessage(_connectionId, method, args));
            _context.Invocations.Add(new RecordedRunnerHubInvocation(_connectionId, method, args));
            var response = _context.ResolveInvocationResponse(method, args);
            if (response is Task<T> pending)
                return pending;
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
