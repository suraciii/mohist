using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Otel;
using EnvironmentAbstractions;
using EnvironmentAbstractions.TestHelpers;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Support;

/// <summary>
/// A lightweight test fixture that exposes the production service graph
/// (DI + EF) without spinning up <c>WebApplicationFactory&lt;Program&gt;</c>
/// or an Orleans silo. Use this for service-level specs that need
/// <c>_fixture.Services.CreateScope()</c> but never call <c>HttpClient</c>
/// or any Orleans grain.
/// </summary>
/// <remarks>
/// Shares the production service graph via
/// <see cref="MohistServiceRegistration.ConfigureMohistServices"/>, so any
/// drift in production registrations is caught by tests using this fixture.
/// Grains are not available here; use <c>WorkflowGrainFixture</c> for that.
/// </remarks>
public sealed class MohistDbFixture : IAsyncLifetime
{
    private readonly InMemoryEventBus _eventBus = new(
        new NoopEventStore(),
        NullLogger<InMemoryEventBus>.Instance);
    private readonly RecordingEventStore _eventStore = new();
    private SqliteConnection _keeper = null!;
    private SqliteConnection _otelKeeper = null!;
    private IServiceProvider? _services;
    private string? _connectionString;

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("MohistDbFixture is not initialized");
    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("MohistDbFixture is not initialized");
    public InMemoryEventBus EventBus => _eventBus;
    public IEventPublisher EventPublisher => _eventBus;
    public RecordingEventStore EventStore => _eventStore;

    /// <summary>
    /// Grain factory is not provided by this fixture. Specs that exercise
    /// Orleans grains directly must use <c>WorkflowGrainFixture</c> instead.
    /// Throws to surface the misuse rather than returning a half-working
    /// client.
    /// </summary>
    public IGrainFactory Grains => throw new NotSupportedException(
        "MohistDbFixture does not host an Orleans silo. " +
        "Use WorkflowGrainFixture for grain-level tests.");

    public Task InitializeAsync()
    {
        var dbName = $"mohist-dbspec-{Guid.NewGuid():N}";
        _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();

        const string runnerRoot = "/test/runner";
        const string systemUpdateStatePath = "/test/system-update.json";
        const string artifactStorageRoot = "/test/artifacts";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:RunnerRoot"] = runnerRoot,
                ["Mohist:WebRoot"] = "/test/web",
                ["Mohist:SystemUpdate:StatePath"] = systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = artifactStorageRoot,
                ["Mohist:ServerUrl"] = "http://127.0.0.1:3456",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.ConfigureMohistServices(config);

        // Test-only overrides so the fixture doesn't touch the real
        // filesystem, the real git, the real env vars.
        services.RemoveAll<IGitService>();
        services.AddSingleton<FakeGitService>();
        services.AddSingleton<IGitService>(sp => sp.GetRequiredService<FakeGitService>());
        services.RemoveAll<IEnvironmentVariableProvider>();
        services.AddSingleton<IEnvironmentVariableProvider, MockEnvironmentVariableProvider>();
        // IEventPublisher is shared so all tests in the same fixture see
        // each other's emissions, mirroring MohistIntegrationFixture's
        // behaviour. IEventStore is left as the real production
        // implementation so its DB writes are visible to the test's
        // query scope.
        services.RemoveAll<IEventPublisher>();
        services.AddSingleton<IEventPublisher>(_eventBus);
        // RecordingEventStore remains available via the EventStore
        // property for specs that explicitly want to assert on recorded
        // calls; the in-scope IEventStore is the real one.

        // Replace the file-backed production OtelDb with an in-memory instance
        // so the service graph never creates a real otel.db file
        // (design/testing.md hard-constraint 1). The keeper connection keeps
        // the database alive until DisposeAsync.
        var (otelDb, otelKeeper) = InMemoryOtelDb.Create();
        _otelKeeper = otelKeeper;
        services.RemoveAll<OtelDb>();
        services.AddSingleton(otelDb);

        _services = services.BuildServiceProvider();

        // The schema must be identical to production (the EF migrations in
        // Infrastructure/Data/Migrations; EnsureCreated would skip them and
        // produce a different schema for SQLite, breaking IDENTITY columns
        // and computed columns). The migrated-template clone is exactly the
        // Migrate() output without re-running the chain here.
        MigratedSqliteTemplate.CopyTo(_keeper);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _services = null;
        _keeper?.Dispose();
        _otelKeeper?.Dispose();
        return Task.CompletedTask;
    }
}
