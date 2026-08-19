using Mohist.Server.SpecTests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Mohist.Server.TestSupport;
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
    // These are logical TestServer listener identities, not OS ports.
    // TestServer and the in-memory exporter never bind them.
    public const int OtlpPort = 0;
    public const int DisabledOtlpPort = 1;

    private SqliteConnection _keeper = null!;
    public OtlpRoutesWebApplicationFactory Factory { get; private set; } = null!;
    public OtlpRoutesWebApplicationFactory DisabledFactory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = $"Data Source=otel-int-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        await _keeper.OpenAsync();
        MigratedSqliteTemplate.CopyTo(_keeper);

        Factory = new OtlpRoutesWebApplicationFactory(
            connectionString,
            "/mohist-tests/otel/runner",
            "/mohist-tests/otel/system-update.json",
            OtlpPort);
        await Factory.EnsureSchemaAsync();

        DisabledFactory = new OtlpRoutesWebApplicationFactory(
            connectionString,
            "/mohist-tests/otel/runner",
            "/mohist-tests/otel/system-update.json",
            DisabledOtlpPort,
            otelEnabled: false);
        await DisabledFactory.EnsureSchemaAsync();

        // Force the server to materialize so middleware and routes are
        // registered (MohistWebApplicationFactory is lazy by default).
        _ = Factory.Services;
        _ = DisabledFactory.Services;
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

        Factory.Services.GetRequiredService<RuntimeObservability>().PublishCollector(CollectorResult.Online());
        Factory.Services.GetRequiredService<RuntimeObservability>().ResetTelemetryCountersForTesting();
        Factory.Services.GetRequiredService<OtlpRequestBodyReadProbe>().Reset();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        DisabledFactory?.Dispose();
        await _keeper.DisposeAsync();
    }
}
