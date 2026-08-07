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
using Orleans.Configuration;
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
    private readonly int _siloPort;
    private readonly int _gatewayPort;
    private readonly bool? _otelEnabled;

    public int OtlpPort { get; }
    public FakeOtelQueryExecutor FakeQueryExecutor => Services.GetRequiredService<FakeOtelQueryExecutor>();
    public FakeTimeProvider TimeProvider { get; private set; } = null!;

    public OtlpRoutesWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        int otlpPort = 4318,
        int? siloPort = null,
        int? gatewayPort = null,
        bool? otelEnabled = null)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        OtlpPort = otlpPort;
        _siloPort = siloPort ?? EndpointOptions.DEFAULT_SILO_PORT;
        _gatewayPort = gatewayPort ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
        _otelEnabled = otelEnabled;
        (_otelDb, _otelKeeper) = InMemoryOtelDb.Create();
    }

    /// <summary>The in-memory <see cref="OtelDb"/> shared by the integration specs.</summary>
    public OtelDb OtelDb => _otelDb;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(MohistHostEnvironment.Testing);
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", "/mohist-tests/otel/artifacts");
        if (_otelEnabled is { } otelEnabled)
            builder.UseSetting("Mohist:Otel:Enabled", otelEnabled ? "true" : "false");
        builder.UseSetting("Mohist:Otel:Port", OtlpPort.ToString());
        builder.UseSetting("Mohist:ServerUrl", "http://127.0.0.1:3456");
        builder.UseSetting("Mohist:Silo:SiloPort", _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Mohist:Silo:GatewayPort", _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = "/mohist-tests/otel/artifacts",
                ["Mohist:Otel:Port"] = OtlpPort.ToString(),
                ["Mohist:Silo:SiloPort"] = _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:Silo:GatewayPort"] = _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            services.RemoveAll<IOtelQueryExecutor>();
            services.AddSingleton<FakeOtelQueryExecutor>();
            services.AddSingleton<IOtelQueryExecutor>(provider =>
                provider.GetRequiredService<FakeOtelQueryExecutor>());
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
        var client = base.CreateDefaultClient(new LocalPortHandler(3456));
        client.DefaultRequestHeaders.Host = "localhost:3456";
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

public sealed class FakeOtelQueryExecutor : IOtelQueryExecutor
{
    private readonly TraceQuerier _fallback;
    private TaskCompletionSource<bool>? _block;

    public FakeOtelQueryExecutor(TraceQuerier fallback)
    {
        _fallback = fallback;
    }

    public bool CancellationObserved { get; private set; }
    public Task Blocked => _block?.Task ?? Task.CompletedTask;

    public void BlockNextExecution()
    {
        _block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationObserved = false;
    }

    public async Task<QueryResult> Execute(string sql, CancellationToken cancellationToken = default)
    {
        var block = Interlocked.Exchange(ref _block, null);
        if (block is null)
            return await _fallback.Execute(sql, cancellationToken);

        try
        {
            BlockStarted.TrySetResult(true);
            await block.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancellationObserved = true;
            throw;
        }

        return new QueryResult([], [], false, null);
    }

    public TaskCompletionSource<bool> BlockStarted { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
