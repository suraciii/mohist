using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Otel;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

/// <summary>
/// Lightweight WebApplicationFactory that exposes the OTLP port's
/// <c>POST /otel/v1/traces</c> route in addition to the main API. Uses
/// <c>Microsoft.AspNetCore.TestHost</c> so the test client talks to the
/// pipeline without actually binding a socket; the host header is set
/// explicitly by the test to exercise the <c>RequireHost</c> filter and
/// the <see cref="OtelPortIsolationMiddleware"/>.
/// </summary>
public class OtlpRoutesWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _runnerRoot;
    private readonly string _systemUpdateStatePath;
    private readonly SqliteConnection _otelKeeper;
    private readonly OtelDb _otelDb;
    private readonly bool? _otelEnabled;

    public int OtlpPort { get; }
    public FakeTimeProvider TimeProvider { get; private set; } = null!;

    public OtlpRoutesWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        // TestServer does not bind a socket. Port 0 is therefore a logical
        // listener identity used only by the isolation middleware/header.
        int otlpPort = 0,
        bool? otelEnabled = null)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        OtlpPort = otlpPort;
        _otelEnabled = otelEnabled;
        (_otelDb, _otelKeeper) = InMemoryOtelDb.Create();
    }

    /// <summary>The in-memory <see cref="OtelDb"/> shared by the integration specs.</summary>
    public OtelDb OtelDb => _otelDb;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Keep the HTTP side in TestServer. If a caller accidentally causes a
        // real host path, port 0 delegates selection to the OS instead of
        // touching the production 3456 listener.
        builder.UseTestServer();
        builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        builder.UseEnvironment(MohistHostEnvironment.Testing);
        builder.UseSetting("Mohist:Testing:InMemoryOrleansTransport", "true");
        builder.UseSetting("Mohist:ServerUrl", "http://127.0.0.1:0");
        builder.UseSetting("Mohist:Otel:Endpoint", "http://127.0.0.1:0/otel");
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", "/mohist-tests/otel/artifacts");
        builder.UseSetting("Mohist:Otel:ExportEnabled", "false");
        if (_otelEnabled is { } otelEnabled)
            builder.UseSetting("Mohist:Otel:Enabled", otelEnabled ? "true" : "false");
        builder.UseSetting("Mohist:Otel:Port", OtlpPort.ToString());

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:ServerUrl"] = "http://127.0.0.1:0",
                ["Mohist:Otel:Endpoint"] = "http://127.0.0.1:0/otel",
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = "/mohist-tests/otel/artifacts",
                ["Mohist:Otel:ExportEnabled"] = "false",
                ["Mohist:Testing:InMemoryOrleansTransport"] = "true",
                ["Mohist:Otel:Port"] = OtlpPort.ToString(),
                ["Mohist:AgentJob:DispatchBackoffInitial"] = "00:00:00.050",
                ["Mohist:AgentJob:DispatchBackoffCap"] = "00:00:00.200",
                ["Mohist:AgentJob:DispatchRetryBound"] = "00:00:05",
                ["Mohist:AgentJob:JobTimeout"] = "00:00:08",
                ["Mohist:Notifications:Hermes:WebhookUrl"] = null,
                ["Mohist:OperatorToken"] = MohistIntegrationFixture.OperatorToken,
                ["Mohist:AdminToken"] = MohistIntegrationFixture.AdminToken,
            };
            if (_otelEnabled is { } otelEnabled)
                values["Mohist:Otel:Enabled"] = otelEnabled ? "true" : "false";
            config.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileCredentialStore>();
            services.AddSingleton<IFileCredentialStore>(new InMemoryFileCredentialStore());
            services.RemoveAll<IWebContentProvider>();
            services.AddSingleton<IWebContentProvider, InMemoryWebContentProvider>();
            services.RemoveAll<Mohist.Server.SystemInfo.IFileSystem>();
            services.AddSingleton<Mohist.Server.SystemInfo.IFileSystem, InMemoryServerFileSystem>();
            services.RemoveAll<ISystemUpdateStore>();
            services.AddSingleton<ISystemUpdateStore, InMemorySystemUpdateStore>();
            services.RemoveAll<IManagedAssetCatalog>();
            services.AddSingleton<IManagedAssetCatalog, InMemoryManagedAssetCatalog>();
            services.RemoveAll<IRunnerWorkspaceClient>();
            services.AddSingleton<FakeRunnerWorkspaceClient>();
            services.AddSingleton<IRunnerWorkspaceClient>(provider => provider.GetRequiredService<FakeRunnerWorkspaceClient>());
            services.RemoveAll<IEnvironmentVariableProvider>();
            services.AddSingleton<IEnvironmentVariableProvider>(_ =>
            {
                var env = new MockEnvironmentVariableProvider();
                env[MohistWorkspaceLayout.RunnerRootEnvironmentVariable] = _runnerRoot;
                return env;
            });
            services.RemoveAll<IDbContextFactory<MohistDbContext>>();
            services.AddDbContextFactory<MohistDbContext>(options =>
                options
                    .UseSqlite(_connectionString));
            services.RemoveAll<OtelDb>();
            services.AddSingleton(_otelDb);
            services.RemoveAll<TimeProvider>();
            TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));
            services.AddSingleton<TimeProvider>(TimeProvider);
            services.PostConfigure<OtlpExporterOptions>("tracing", ConfigureInMemoryOtlpExporter);
            services.PostConfigure<OtlpExporterOptions>("metrics", ConfigureInMemoryOtlpExporter);
            services.AddSingleton<OtlpRequestBodyReadProbe>();
            services.AddSingleton<IStartupFilter, OtlpRequestBodyProbeStartupFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _otelKeeper.Dispose();
        }
        base.Dispose(disposing);
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        return client;
    }

    /// <summary>
    /// Creates an HTTP client whose requests carry a Host header that
    /// matches the OTLP port filter (so the OTLP route group is eligible).
    /// </summary>
    public HttpClient CreateOtlpClient()
    {
        var client = base.CreateDefaultClient(new LocalPortHandler(OtlpPort));
        // The test server's default Host is "localhost" with no port.
        // RequireHost("<host>:<port>") matches by exact string — so we
        // must set the host header to the OTLP port configuration.
        client.DefaultRequestHeaders.Host = $"localhost:{OtlpPort}";
        return client;
    }

    /// <summary>
    /// Creates an HTTP client whose requests carry a Host header that
    /// does NOT match the OTLP port — used to verify the OTLP routes
    /// are not reachable from the main API host.
    /// </summary>
    public HttpClient CreateMainApiClient()
    {
        // Port 1 is a logical non-OTLP identity only; TestServer never binds
        // it and the request is routed entirely in-process.
        var client = base.CreateDefaultClient(new LocalPortHandler(1));
        client.DefaultRequestHeaders.Host = "localhost:1";
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        return client;
    }

    private sealed class LocalPortHandler : DelegatingHandler
    {
        private readonly int _localPort;

        public LocalPortHandler(int localPort)
        {
            _localPort = localPort;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Mohist-Test-Local-Port", _localPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static void ConfigureInMemoryOtlpExporter(OtlpExporterOptions options)
    {
        options.ExportProcessorType = ExportProcessorType.Simple;
        options.HttpClientFactory = () => new HttpClient(new InMemoryOtlpExporterHandler());
    }

    public Task EnsureSchemaAsync() => Task.CompletedTask;

}

public sealed class OtlpRequestBodyReadProbe
{
    public const string HeaderName = "X-Mohist-Test-Probe-Body";

    private int _readCount;

    public bool WasRead => Volatile.Read(ref _readCount) != 0;

    public void Reset() => Interlocked.Exchange(ref _readCount, 0);

    public Stream Wrap(Stream inner) => new ReadProbeStream(inner, this);

    private void RecordRead() => Interlocked.Increment(ref _readCount);

    private sealed class ReadProbeStream(Stream inner, OtlpRequestBodyReadProbe probe) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
        {
            probe.RecordRead();
            return inner.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            probe.RecordRead();
            return inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

public sealed class OtlpRequestBodyProbeStartupFilter(OtlpRequestBodyReadProbe probe) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Headers.ContainsKey(OtlpRequestBodyReadProbe.HeaderName))
                context.Request.Body = probe.Wrap(context.Request.Body);
            await continuation();
        });
        next(app);
    };
}
