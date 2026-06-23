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
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Support;

public class MohistIntegrationFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;
    private MohistWebApplicationFactory _factory = null!;
    private string? _runnerRoot;
    private string? _systemUpdateStatePath;

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public IEventPublisher EventBus => _factory.Services.GetRequiredService<IEventPublisher>();
    public FakeGitService Git => _factory.Services.GetRequiredService<FakeGitService>();
    public FakeRunnerWorkspaceClient RunnerWorkspace => _factory.Services.GetRequiredService<FakeRunnerWorkspaceClient>();
    public string ConnectionString { get; private set; } = null!;
    public string RunnerRoot => _runnerRoot ?? throw new InvalidOperationException("Fixture is not initialized");

    public async Task InitializeAsync()
    {
        var dbName = $"mohist-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        await _keeper.OpenAsync();
        _runnerRoot = Path.Combine(Path.GetTempPath(), $"mohist-runner-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runnerRoot);
        _systemUpdateStatePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");

        _factory = new MohistWebApplicationFactory(ConnectionString, _runnerRoot, _systemUpdateStatePath);
        Client = _factory.CreateClient();
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
    }
}

public class MohistWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _runnerRoot;
    private readonly string _systemUpdateStatePath;
    private readonly string _configPath;
    private readonly string _artifactStorageRoot;
    private string? _webRoot;

    public string ArtifactStorageRoot => _artifactStorageRoot;

    public MohistWebApplicationFactory(string connectionString, string runnerRoot, string systemUpdateStatePath)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        _configPath = Path.Combine(Path.GetTempPath(), $"mohist-config-{Guid.NewGuid():N}.jsonc");
        _artifactStorageRoot = Path.Combine(Path.GetTempPath(), $"mohist-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactStorageRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _webRoot ??= CreateWebRoot();
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:WebRoot", _webRoot);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", _artifactStorageRoot);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:WebRoot"] = _webRoot,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = _artifactStorageRoot,
                ["Mohist:AgentJob:DispatchBackoffInitial"] = "00:00:00.050",
                ["Mohist:AgentJob:DispatchBackoffCap"] = "00:00:00.200",
                ["Mohist:AgentJob:DispatchRetryBound"] = "00:00:05",
                ["Mohist:AgentJob:JobTimeout"] = "00:00:08",
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
        });
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
    }

    private static string CreateWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html><body>Mohist Test Web</body></html>");
        return root;
    }
}

public sealed class RecordingRunnerHubContext : IHubContext<RunnerHub>
{
    private readonly RecordingHubClients _clients;

    public RecordingRunnerHubContext()
    {
        _clients = new RecordingHubClients(this);
    }

    public List<RecordedRunnerHubMessage> SentMessages { get; } = [];
    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new NoopGroupManager();

    public void Clear() => SentMessages.Clear();

    private sealed class RecordingHubClients : IHubClients
    {
        private readonly RecordingRunnerHubContext _context;

        public RecordingHubClients(RecordingRunnerHubContext context)
        {
            _context = context;
        }

        public IClientProxy All => new RecordingClientProxy(_context, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, "all-except");
        public IClientProxy Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy(_context, string.Join(",", connectionIds));
        public IClientProxy Group(string groupName) => new RecordingClientProxy(_context, groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy(_context, string.Join(",", groupNames));
        public IClientProxy User(string userId) => new RecordingClientProxy(_context, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy(_context, string.Join(",", userIds));
    }

    private sealed class RecordingClientProxy : IClientProxy
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
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed record RecordedRunnerHubMessage(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);
