using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

/// <summary>
/// One <see cref="OtlpRoutesWebApplicationFactory"/> (web host + silo)
/// shared by the whole IntegrationTelemetry collection. The OTLP/query
/// specs previously stood up a fresh factory per test, which put this
/// collection on the run's critical path (~4s host start × every test,
/// serially). Tests isolate through <see cref="ResetOtelStateAsync"/> —
/// the OTLP surface's only cross-test state is the two otel tables and
/// the <see cref="OtelCollectorStatus"/> singleton.
/// </summary>
public sealed class OtlpRoutesHostFixture : IAsyncLifetime
{
    public const int OtlpPort = 14318;

    private SqliteConnection _keeper = null!;
    private string _runnerRoot = null!;
    private string _systemUpdateStatePath = null!;
    private TestClusterPortAllocator? _portAllocator;

    public OtlpRoutesWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=otel-int-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        await _keeper.OpenAsync();
        MigratedSqliteTemplate.CopyTo(_keeper);

        _runnerRoot = Path.Combine(Path.GetTempPath(), $"mohist-runner-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runnerRoot);
        _systemUpdateStatePath = Path.Combine(Path.GetTempPath(), $"mohist-sys-otel-{Guid.NewGuid():N}.json");

        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);

        Factory = new OtlpRoutesWebApplicationFactory(
            connectionString,
            _runnerRoot,
            _systemUpdateStatePath,
            OtlpPort,
            siloPort,
            gatewayPort);
        // Force the server to materialize so middleware and routes are
        // registered (MohistWebApplicationFactory is lazy by default).
        _ = Factory.Services;
    }

    /// <summary>
    /// Returns the shared host to its post-startup state: empty otel
    /// tables and a bound collector port (the production happy path flips
    /// IsPortBound to true during start). Every test in the collection
    /// runs this first, so assertions on "empty database" or table counts
    /// stay exact.
    /// </summary>
    public Task ResetOtelStateAsync()
    {
        var db = Factory.Services.GetRequiredService<OtelDb>();
        using (var connection = db.OpenReadWriteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM {OtelDb.SpansTable}; DELETE FROM {OtelDb.TracesTable};";
            cmd.ExecuteNonQuery();
        }

        Factory.Services.GetRequiredService<OtelCollectorStatus>().SetPortBound(true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        _portAllocator?.Dispose();
        await _keeper.DisposeAsync();
        try { if (Directory.Exists(_runnerRoot)) Directory.Delete(_runnerRoot, recursive: true); } catch { }
        try { if (File.Exists(_systemUpdateStatePath)) File.Delete(_systemUpdateStatePath); } catch { }
    }
}
