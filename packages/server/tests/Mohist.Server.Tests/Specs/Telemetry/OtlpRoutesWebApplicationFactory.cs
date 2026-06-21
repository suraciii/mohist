using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Otel;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.Telemetry;

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
    private readonly string _otelDbPath;
    private readonly string _artifactStorageRoot;
    private string? _webRoot;

    public int OtlpPort { get; }

    public OtlpRoutesWebApplicationFactory(
        string connectionString,
        string runnerRoot,
        string systemUpdateStatePath,
        int otlpPort = 4318)
    {
        _connectionString = connectionString;
        _runnerRoot = runnerRoot;
        _systemUpdateStatePath = systemUpdateStatePath;
        OtlpPort = otlpPort;
        _otelDbPath = Path.Combine(Path.GetTempPath(), $"mohist-otel-int-{Guid.NewGuid():N}.db");
        _artifactStorageRoot = Path.Combine(Path.GetTempPath(), $"mohist-artifacts-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactStorageRoot);
    }

    public string OtlpDbPath => _otelDbPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _webRoot ??= CreateWebRoot();
        builder.UseSetting("Mohist:SqliteConnectionString", _connectionString);
        builder.UseSetting("Mohist:WebRoot", _webRoot);
        builder.UseSetting("Mohist:RunnerRoot", _runnerRoot);
        builder.UseSetting("Mohist:SystemUpdate:StatePath", _systemUpdateStatePath);
        builder.UseSetting("Mohist:ArtifactStorage:Root", _artifactStorageRoot);
        builder.UseSetting("Mohist:Otel:Port", OtlpPort.ToString());
        builder.UseSetting("Mohist:Otel:DbPath", _otelDbPath);
        builder.UseSetting("Mohist:ServerUrl", "http://127.0.0.1:3456");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = _connectionString,
                ["Mohist:WebRoot"] = _webRoot,
                ["Mohist:RunnerRoot"] = _runnerRoot,
                ["Mohist:SystemUpdate:StatePath"] = _systemUpdateStatePath,
                ["Mohist:ArtifactStorage:Root"] = _artifactStorageRoot,
                ["Mohist:Otel:Port"] = OtlpPort.ToString(),
                ["Mohist:Otel:DbPath"] = _otelDbPath,
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
                    .UseSqlite(_connectionString)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        });
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
    }

    private static string CreateWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-web-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html><body>Mohist OTel Test Web</body></html>");
        return root;
    }
}
