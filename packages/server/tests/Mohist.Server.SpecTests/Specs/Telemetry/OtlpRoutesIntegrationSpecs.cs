using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

[Collection("IntegrationTelemetry")]
public class OtlpRoutesIntegrationSpecs : IAsyncLifetime
{
    private const int OtlpPort = OtlpRoutesHostFixture.OtlpPort;
    private const string OtlpPath = "/otel/v1/traces";

    private readonly OtlpRoutesHostFixture _fixture;
    private OtlpRoutesWebApplicationFactory _factory => _fixture.Factory;

    public OtlpRoutesIntegrationSpecs(OtlpRoutesHostFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetOtelStateAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MissingEnablement_PostValidJson_IngestPayload_Returns200AndEmptyObject()
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
        using var document = JsonDocument.Parse(body);
        Assert.Equal(3, document.RootElement.GetProperty("code").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("details", out _));
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


}
