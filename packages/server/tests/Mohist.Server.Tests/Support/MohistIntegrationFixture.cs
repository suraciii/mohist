using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events;
using Xunit;

namespace Mohist.Server.Tests.Support;

public class MohistIntegrationFixture : IAsyncLifetime
{
    private readonly InMemoryEventBus _eventBus = new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);

    private SqliteConnection _keeper = null!;
    private MohistWebApplicationFactory _factory = null!;
    private string? _runnerRoot;

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public IEventBus EventBus => _eventBus;
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

        _factory = new MohistWebApplicationFactory(ConnectionString, _eventBus, _runnerRoot);
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
    }
}

public class MohistWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IEventBus _eventBus;
    private readonly string _runnerRoot;
    private string? _webRoot;

    public MohistWebApplicationFactory(string connectionString, IEventBus eventBus, string runnerRoot)
    {
        _connectionString = connectionString;
        _eventBus = eventBus;
        _runnerRoot = runnerRoot;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _webRoot ??= CreateWebRoot();
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:WebRoot", _webRoot);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:WebRoot"] = _webRoot,
                ["Mohist:RunnerRoot"] = _runnerRoot,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEventBus>(_eventBus);
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

[CollectionDefinition("MohistIntegration", DisableParallelization = true)]
public class MohistIntegrationCollection : ICollectionFixture<MohistIntegrationFixture>;
