using Mohist.Server.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Logging;
using Mohist.Server.Otel;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Services.Prompts;
using EnvironmentAbstractions;
using EnvironmentAbstractions.TestHelpers;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Support;

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
        new FakeTimeProvider(TestTime.UtcNow),
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

    public ValueTask InitializeAsync()
    {
        var dbName = $"mohist-dbspec-{Guid.NewGuid():N}";
        _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();

        const string runnerRoot = "/mohist-tests/runner";
        const string systemUpdateStatePath = "/mohist-tests/system-update.json";
        const string artifactStorageRoot = "/mohist-tests/artifacts";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:RunnerRoot"] = runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = artifactStorageRoot,
                // This fixture never starts a web listener. Keep the value a
                // logical TestServer identity; no socket is opened.
                ["Mohist:ServerUrl"] = "http://localhost",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.ConfigureMohistServices(config);

        // Test-only overrides so the fixture doesn't touch the real
        // filesystem or the real env vars.
        services.RemoveAll<IFileSystem>();
        services.AddSingleton<IFileSystem, InMemoryServerFileSystem>();
        services.RemoveAll<ISystemUpdateStore>();
        services.AddSingleton<InMemorySystemUpdateStore>();
        services.AddSingleton<ISystemUpdateStore>(sp => sp.GetRequiredService<InMemorySystemUpdateStore>());
        services.RemoveAll<IAttachmentStorage>();
        services.AddSingleton<InMemoryAttachmentStorage>();
        services.AddSingleton<IAttachmentStorage>(sp => sp.GetRequiredService<InMemoryAttachmentStorage>());
        services.RemoveAll<IWorkflowArtifactStorage>();
        services.AddSingleton<InMemoryWorkflowArtifactStorage>();
        services.AddSingleton<IWorkflowArtifactStorage>(sp => sp.GetRequiredService<InMemoryWorkflowArtifactStorage>());
        services.RemoveAll<IWebContentProvider>();
        services.AddSingleton<IWebContentProvider, InMemoryWebContentProvider>();
        services.RemoveAll<IPromptLoader>();
        services.AddSingleton<IPromptLoader>(_ => new InMemoryPromptLoader());
        services.RemoveAll<ILogTailSource>();
        services.AddSingleton<InMemoryLogTailSource>();
        services.AddSingleton<ILogTailSource>(provider => provider.GetRequiredService<InMemoryLogTailSource>());
        services.RemoveAll<IEnvironmentVariableProvider>();
        services.AddSingleton<IEnvironmentVariableProvider, MockEnvironmentVariableProvider>();
        services.RemoveAll<Mohist.Server.Infrastructure.Config.IConfigDocumentStore>();
        services.AddSingleton<InMemoryConfigDocumentStore>();
        services.AddSingleton<Mohist.Server.Infrastructure.Config.IConfigDocumentStore>(sp => sp.GetRequiredService<InMemoryConfigDocumentStore>());
        // IEventPublisher is shared so all tests in the same fixture see
        // each other's emissions, mirroring MohistIntegrationFixture's
        // behaviour. IEventStore is left as the real production
        // implementation so its DB writes are visible to the test's
        // query scope.
        services.RemoveAll<IEventPublisher>();
        services.AddSingleton<IEventPublisher>(_eventBus);
        // Issue-362 T-003: the three event producers (WorkflowRunStore,
        // IssueStore, AgentSessionStore) take an IGrainFactory in their
        // constructor so they can poke the dispatcher grain after commit.
        // MohistDbFixture has no silo, so register a no-op grain factory
        // whose dispatcher reference is a no-op. The poke is a pure
        // latency optimization — a missing poke is invisible to specs
        // that exercise the stores without the dispatcher.
        services.AddSingleton<IGrainFactory, NullDispatchGrainFactory>();
        // RunnerStatusService internally calls IRunnerRegistryGrain via the
        // grain factory, which the null dispatch above rejects. Replace the
        // service with a fake that returns an empty runner list by default
        // and lets specs seed runners via the RunnerStatus property when
        // they need to assert on global runner projection.
        services.RemoveAll<Mohist.Server.Runner.Services.RunnerStatusService>();
        services.AddSingleton<NoopRunnerStatusService>();
        services.AddSingleton<Mohist.Server.Runner.Services.RunnerStatusService>(sp => sp.GetRequiredService<NoopRunnerStatusService>());
        // RecordingEventStore remains available via the EventStore
        // property for specs that explicitly want to assert on recorded
        // calls; the in-scope IEventStore is the real one.

        // Replace the file-backed production OtelDb with an in-memory instance
        // so the service graph never creates a real otel.db file
        // (design/testing.md "No External Environment"). The keeper connection keeps
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
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _services = null;
        _keeper?.Dispose();
        _otelKeeper?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stand-in for
    /// <see cref="MohistDbFixture"/>, which has no silo. The dispatcher
    /// grain is a no-op reference so the stores' post-commit poke runs
    /// without an Orleans activation. The poke is a pure latency
    /// optimization — its absence is invisible to specs that exercise
    /// the stores directly without driving the dispatcher.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

        public GrainId GrainId => default;
        public string Key => string.Empty;
    }

    /// <summary>
    /// Replaces <see cref="Mohist.Server.Runner.Services.RunnerStatusService"/> in
    /// this fixture: the production service requires the silo-backed
    /// <c>IRunnerRegistryGrain</c>, which the null dispatch grain factory
    /// above rejects. The fake returns an empty runner list by default and
    /// lets specs seed runners via the <see cref="SetRunners"/> method
    /// when they need to assert on global runner projection.
    /// </summary>
    public sealed class NoopRunnerStatusService : Mohist.Server.Runner.Services.RunnerStatusService
    {
        private readonly object _gate = new();
        private IReadOnlyList<Mohist.Server.Runner.Services.RunnerStatusView> _runners =
            Array.Empty<Mohist.Server.Runner.Services.RunnerStatusView>();

        public NoopRunnerStatusService()
            : base(
                grainFactory: new NullDispatchGrainFactory(),
                connectionTracker: new Mohist.Server.Runner.Services.RunnerConnectionTracker(),
                timeProvider: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Mohist.Server.TestSupport.TestTime.UtcNow))
        {
        }

        public void SetRunners(IEnumerable<Mohist.Server.Runner.Services.RunnerStatusView> runners)
        {
            lock (_gate)
            {
                _runners = runners.ToArray();
            }
        }

        public override Task<IReadOnlyList<Mohist.Server.Runner.Services.RunnerStatusView>> GetRunnersAsync(string projectId)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<Mohist.Server.Runner.Services.RunnerStatusView>>(_runners.ToArray());
            }
        }
    }
}
