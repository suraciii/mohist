using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Integration fixture for event-publishing specs. It replaces the
/// realtime transcript publisher with a recorder and keeps a recording
/// <see cref="IEventPublisher"/> available for tests that still exercise
/// explicit publisher calls. Hosted in its own xUnit collection
/// (<c>EventPublishing</c>) so test-only publishers do not leak into the
/// shared <c>MohistIntegration</c> collection.
/// </summary>
public sealed class EventPublishingIntegrationFixture : IAsyncLifetime
{
    private readonly EventPublishingWebApplicationFactory _factory;
    private readonly string _connectionString;
    private readonly TestClusterPortAllocator _portAllocator;
    private SqliteConnection _keeper = null!;

    public EventPublishingIntegrationFixture()
    {
        _connectionString = $"Data Source=event-publishing-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);
        _factory = new EventPublishingWebApplicationFactory(
            _connectionString,
            "/mohist-tests/event-publishing/runner",
            "/mohist-tests/event-publishing/system-update.json",
            "/mohist-tests/event-publishing/logs",
            siloPort,
            gatewayPort);
    }

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client => _factory.CreateClient();
    public IServiceProvider Services => _factory.Services;
    public RecordingIEventPublisher RecordingPublisher => _factory.RecordingPublisher;
    public RecordingTranscriptEventPublisher RecordingTranscriptPublisher => _factory.RecordingTranscriptPublisher;

    public async Task InitializeAsync()
    {
        // Hold a connection open to the shared-cache in-memory database so
        // it survives between the Orleans AdoNet storage's reads/writes
        // and our test's queries. Mirrors MohistIntegrationFixture.
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();
        await _factory.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
        _portAllocator.Dispose();
    }

    private sealed class EventPublishingWebApplicationFactory : MohistWebApplicationFactory
    {
        public RecordingIEventPublisher RecordingPublisher { get; }
        public RecordingTranscriptEventPublisher RecordingTranscriptPublisher { get; }

        public EventPublishingWebApplicationFactory(string connectionString, string runnerRoot, string systemUpdateStatePath, string logsPath, int siloPort, int gatewayPort)
            : base(connectionString, runnerRoot, systemUpdateStatePath, logsPath, timeProvider: null, siloPort, gatewayPort)
        {
            RecordingPublisher = new RecordingIEventPublisher(new NoopEventPublisher());
            RecordingTranscriptPublisher = new RecordingTranscriptEventPublisher();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEventPublisher>();
                services.AddSingleton<IEventPublisher>(RecordingPublisher);
                services.RemoveAll<ITranscriptEventPublisher>();
                services.AddSingleton<ITranscriptEventPublisher>(RecordingTranscriptPublisher);
            });
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<TData>(
            TData data,
            string type,
            string source,
            string? subject = null,
            IReadOnlyDictionary<string, string>? extensions = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    public sealed class RecordingTranscriptEventPublisher : ITranscriptEventPublisher
    {
        public List<TranscriptEnvelope> Published { get; } = [];

        public void Clear() => Published.Clear();

        public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default)
        {
            Published.Add(envelope);
            return Task.CompletedTask;
        }
    }
}
