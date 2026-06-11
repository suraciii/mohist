using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events;

/// <summary>
/// Integration fixture for the SignalR event-publishing spec tests.
/// Wraps the production <see cref="IEventPublisher"/> with a
/// <see cref="RecordingIEventPublisher"/> so tests can assert on
/// <c>PublishAsync</c> call counts. Hosted in its own xUnit collection
/// (<c>EventPublishing</c>) so the recording publisher does not leak
/// into the shared <c>MohistIntegration</c> collection.
/// </summary>
public sealed class EventPublishingIntegrationFixture : IAsyncLifetime
{
    private readonly EventPublishingWebApplicationFactory _factory;
    private readonly string _runnerRoot;
    private readonly string _systemUpdateStatePath;
    private readonly string _connectionString;
    private SqliteConnection _keeper = null!;

    public EventPublishingIntegrationFixture()
    {
        _connectionString = $"Data Source=event-publishing-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _runnerRoot = Path.Combine(Path.GetTempPath(), $"mohist-runner-evp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runnerRoot);
        _systemUpdateStatePath = Path.Combine(Path.GetTempPath(), $"mohist-sys-evp-{Guid.NewGuid():N}.json");
        _factory = new EventPublishingWebApplicationFactory(_connectionString, _runnerRoot, _systemUpdateStatePath);
    }

    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public HttpClient Client => _factory.CreateClient();
    public RecordingIEventPublisher RecordingPublisher => _factory.RecordingPublisher;
    public RecordingTranscriptEventPublisher RecordingTranscriptPublisher => _factory.RecordingTranscriptPublisher;

    public async Task InitializeAsync()
    {
        // Hold a connection open to the shared-cache in-memory database so
        // it survives between the Orleans AdoNet storage's reads/writes
        // and our test's queries. Mirrors MohistIntegrationFixture.
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        if (_keeper is not null)
            await _keeper.DisposeAsync();
        if (Directory.Exists(_runnerRoot))
            Directory.Delete(_runnerRoot, recursive: true);
        if (File.Exists(_systemUpdateStatePath))
            File.Delete(_systemUpdateStatePath);
        await Task.CompletedTask;
    }

    private sealed class EventPublishingWebApplicationFactory : MohistWebApplicationFactory
    {
        public RecordingIEventPublisher RecordingPublisher { get; }
        public RecordingTranscriptEventPublisher RecordingTranscriptPublisher { get; }

        public EventPublishingWebApplicationFactory(string connectionString, string runnerRoot, string systemUpdateStatePath)
            : base(connectionString, runnerRoot, systemUpdateStatePath)
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
