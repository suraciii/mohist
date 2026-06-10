using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Workspace;
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
    }
}

public class MohistWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _runnerRoot;
    private readonly string _systemUpdateStatePath;
    private readonly string _configPath;
    private string? _webRoot;

    public MohistWebApplicationFactory(string connectionString, string runnerRoot, string systemUpdateStatePath)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        _configPath = Path.Combine(Path.GetTempPath(), $"mohist-config-{Guid.NewGuid():N}.jsonc");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _webRoot ??= CreateWebRoot();
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:WebRoot", _webRoot);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:WebRoot"] = _webRoot,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGitService>();
            services.AddSingleton<FakeGitService>();
            services.AddSingleton<IGitService>(provider => provider.GetRequiredService<FakeGitService>());
            services.RemoveAll<ConfigService>();
            services.AddSingleton<IEnvironmentVariableProvider, MockEnvironmentVariableProvider>();
            services.AddSingleton(provider => new ConfigService(
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<IEnvironmentVariableProvider>(),
                provider.GetRequiredService<ILogger<ConfigService>>(),
                _configPath));
        });
    }

    private static string CreateWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html><body>Mohist Test Web</body></html>");
        return root;
    }
}
