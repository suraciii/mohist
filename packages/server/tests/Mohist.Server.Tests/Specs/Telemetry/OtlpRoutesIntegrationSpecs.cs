using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Telemetry;

[Trait(Traits.Speed.Name, Traits.Speed.Integration)]
[Trait(Traits.Sut.Name, Traits.Sut.Telemetry)]
public class OtlpRoutesIntegrationSpecs : IAsyncLifetime
{
    private const int OtlpPort = 14318;
    private const string OtlpPath = "/otel/v1/traces";

    private SqliteConnection _keeper = null!;
    private OtlpRoutesWebApplicationFactory _factory = null!;
    private string _runnerRoot = null!;
    private string _systemUpdateStatePath = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=otel-int-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();

        _runnerRoot = Path.Combine(Path.GetTempPath(), $"mohist-runner-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runnerRoot);
        _systemUpdateStatePath = Path.Combine(Path.GetTempPath(), $"mohist-sys-otel-{Guid.NewGuid():N}.json");

        _factory = new OtlpRoutesWebApplicationFactory(
            _connectionString,
            _runnerRoot,
            _systemUpdateStatePath,
            OtlpPort);
        await _factory.EnsureSchemaAsync();

        // Force the server to materialize so middleware and routes are
        // registered (MohistWebApplicationFactory is lazy by default).
        _ = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _keeper.DisposeAsync();
        try { if (Directory.Exists(_runnerRoot)) Directory.Delete(_runnerRoot, recursive: true); } catch { }
        try { if (File.Exists(_systemUpdateStatePath)) File.Delete(_systemUpdateStatePath); } catch { }
        try { if (File.Exists(_factory?.OtlpDbPath)) File.Delete(_factory.OtlpDbPath); } catch { }
    }

    [Fact]
    public async Task PostValidJson_IngestPayload_Returns200AndEmptyObject()
    {
        using var client = _factory.CreateOtlpClient();
        const string payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"svc"}}]},
                "scopeSpans": [{
                  "spans": [{
                    "traceId":"00000000000000000000000000000001",
                    "spanId":"0000000000000001",
                    "name":"GET /x",
                    "startTimeUnixNano":"1767225600000000000",
                    "endTimeUnixNano":"1767225601000000000"
                  }]
                }]
              }]
            }
            """;

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{}", body);
    }

    [Fact]
    public async Task PostValidJson_PersistsToOtelDb()
    {
        using var client = _factory.CreateOtlpClient();
        const string traceId = "00000000000000000000000000000002";
        const string payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"persisted-svc"}}]},
                "scopeSpans": [{
                  "spans": [{
                    "traceId":"00000000000000000000000000000002",
                    "spanId":"0000000000000002",
                    "name":"POST /y",
                    "startTimeUnixNano":"1767225600000000000",
                    "endTimeUnixNano":"1767225601000000000"
                  }]
                }]
              }]
            }
            """;

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Open the otel DB directly and verify
        var db = _factory.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
        cmd.Parameters.AddWithValue("$id", traceId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("persisted-svc", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task PostProtobufContentType_Returns415()
    {
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("ignored", Encoding.UTF8, "application/x-protobuf");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task PostUnsupportedContentType_Returns415()
    {
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("ignored", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task PostInvalidJsonBody_Returns400()
    {
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("not json {", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostEmptyJsonBody_Returns200AndNoRows()
    {
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db = _factory.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable}";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task PostToMainApiHost_DoesNotInvokeOtlpRoute()
    {
        // Host header set to the main API port — the OTLP route's
        // RequireHost filter must not match, so the request falls
        // through the pipeline and the isolation middleware then
        // answers 404 because /otel/v1/traces is on the OTLP port only.
        using var client = _factory.CreateMainApiClient();
        const string payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"x"}}]},
                "scopeSpans": [{"spans":[{
                  "traceId":"00000000000000000000000000000009",
                  "spanId":"0000000000000009","name":"a",
                  "startTimeUnixNano":"1767225600000000000",
                  "endTimeUnixNano":"1767225601000000000"
                }]}]
              }]
            }
            """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);

        // Either the isolation middleware short-circuits with 404, or
        // the route group's RequireHost causes the route to not match
        // and the request falls through to the SPA fallback / 404.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostToMainApiPort_WithSpoofedOtlpHost_DoesNotInvokeOtlpRoute()
    {
        using var client = _factory.CreateMainApiClient();
        client.DefaultRequestHeaders.Host = $"localhost:{OtlpPort}";
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostDuplicatePayload_IsIdempotent()
    {
        using var client = _factory.CreateOtlpClient();
        const string traceId = "00000000000000000000000000000099";
        const string payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"idem"}}]},
                "scopeSpans": [{"spans":[{
                  "traceId":"00000000000000000000000000000099",
                  "spanId":"0000000000000099","name":"a",
                  "startTimeUnixNano":"1767225600000000000",
                  "endTimeUnixNano":"1767225601000000000"
                }]}]
              }]
            }
            """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        for (var i = 0; i < 3; i++)
        {
            using var c = new StringContent(payload, Encoding.UTF8, "application/json");
            using var r = await client.PostAsync(OtlpPath, c);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var db = _factory.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadOnlyConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $id";
            cmd.Parameters.AddWithValue("$id", traceId);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
            cmd.Parameters.AddWithValue("$id", traceId);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }
}
