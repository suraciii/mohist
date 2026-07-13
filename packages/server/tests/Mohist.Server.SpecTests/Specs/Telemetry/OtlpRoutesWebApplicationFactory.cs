using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Logging;
using Mohist.Server.Otel;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
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
    private readonly string _artifactStorageRoot;
    private readonly SqliteConnection _otelKeeper;
    private readonly OtelDb _otelDb;
    private readonly int _siloPort;
    private readonly int _gatewayPort;
    private readonly InMemoryHostFileDependencies _fileDependencies = new();
    private readonly InMemoryLogFileStore _logFiles = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    public int OtlpPort { get; }

    public OtlpRoutesWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        int otlpPort = 4318,
        int? siloPort = null,
        int? gatewayPort = null)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        OtlpPort = otlpPort;
        _siloPort = siloPort ?? EndpointOptions.DEFAULT_SILO_PORT;
        _gatewayPort = gatewayPort ?? EndpointOptions.DEFAULT_GATEWAY_PORT;
        // OTel ingester/query storage is backed by an in-memory shared-cache
        // SQLite database so the integration specs never touch a real otel.db
        // file (design/testing.md hard-constraint 1). The keeper connection
        // keeps the database alive for the factory's lifetime; it is disposed
        // in Dispose(bool). The physical read-only contract is not enforced
        // against an in-memory database, which is acceptable here because
        // TraceQuerier only issues SELECTs (the read-only guard is a production
        // CLI safety constraint, not a behavior under test).
        (_otelDb, _otelKeeper) = InMemoryOtelDb.Create();
        _artifactStorageRoot = "/test/artifacts-otel";
    }

    /// <summary>The in-memory <see cref="OtelDb"/> shared by the integration specs.</summary>
    public OtelDb OtelDb => _otelDb;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(MohistHostEnvironment.Testing);
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", _artifactStorageRoot);
        builder.UseSetting("Mohist:Otel:Port", OtlpPort.ToString());
        builder.UseSetting("Mohist:ServerUrl", "http://127.0.0.1:3456");
        builder.UseSetting("Mohist:Silo:SiloPort", _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Mohist:Silo:GatewayPort", _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = _artifactStorageRoot,
                ["Mohist:CliSkillDataPath"] = "/test/skill-data",
                ["Mohist:Otel:Port"] = OtlpPort.ToString(),
                ["Mohist:Silo:SiloPort"] = _siloPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:Silo:GatewayPort"] = _gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mohist:AgentJob:DispatchBackoffInitial"] = "00:00:00.050",
                ["Mohist:AgentJob:DispatchBackoffCap"] = "00:00:00.200",
                ["Mohist:AgentJob:DispatchRetryBound"] = "00:00:05",
                ["Mohist:AgentJob:JobTimeout"] = "00:00:08",
                ["Mohist:Notifications:Hermes:WebhookUrl"] = null,
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
            services.RemoveAll<IEnvironmentVariableProvider>();
            services.AddSingleton<IEnvironmentVariableProvider>(_ =>
            {
                var env = new MockEnvironmentVariableProvider();
                env[MohistWorkspaceLayout.RunnerRootEnvironmentVariable] = _runnerRoot;
                env[SystemInfoService.HomeEnvironmentVariable] = "/test/home";
                return env;
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_timeProvider);
            _fileDependencies.ReplaceServiceRegistrations(
                services,
                "/test/config-otel.jsonc",
                _artifactStorageRoot,
                "/test/attachments-otel",
                _timeProvider);
            services.RemoveAll<ILogFileStore>();
            services.AddSingleton<ILogFileStore>(_logFiles);
            services.RemoveAll<FileLoggerProvider>();
            services.RemoveAll<ILoggerProvider>();
            services.RemoveAll<IDbContextFactory<MohistDbContext>>();
            services.AddDbContextFactory<MohistDbContext>(options =>
                options
                    .UseSqlite(_connectionString));
            // Replace the file-backed production OtelDb with the in-memory
            // instance so the OTLP/query routes exercise the real TraceIngester
            // and TraceQuerier without touching the filesystem.
            services.RemoveAll<OtelDb>();
            services.AddSingleton(_otelDb);
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

}
