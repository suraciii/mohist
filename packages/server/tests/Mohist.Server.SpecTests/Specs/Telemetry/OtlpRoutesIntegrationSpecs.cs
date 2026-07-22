using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
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
    public async Task PostProtobufContentType_PersistsToOtelDb()
    {
        using var client = _factory.CreateOtlpClient();
        const string traceId = "00000000000000000000000000000003";
        var payload = BuildMinimalProtobufTracePayload(traceId, "0000000000000003", "proto", "protobuf span");
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db = _factory.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
        cmd.Parameters.AddWithValue("$id", traceId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("proto", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public void ProtobufPayload_ParsesRequiredSpanFields()
    {
        const string traceId = "00000000000000000000000000000004";
        const string spanId = "0000000000000004";
        var payload = BuildMinimalProtobufTracePayload(traceId, spanId, "protobuf-parser", "protobuf parser span");

        var parsed = Mohist.Server.Otel.OtlpProtobuf.OtlpProtobufTraceParser.Parse(payload);
        var resourceSpans = Assert.Single(parsed.ResourceSpans ?? []);
        var serviceName = Assert.Single(resourceSpans.Resource?.Attributes ?? [], a => a.Key == "service.name");
        Assert.Equal("protobuf-parser", serviceName.Value?.StringValue);
        var scopeSpans = Assert.Single(resourceSpans.ScopeSpans ?? []);
        var span = Assert.Single(scopeSpans.Spans ?? []);
        Assert.Equal(traceId, span.TraceId);
        Assert.Equal(spanId, span.SpanId);
        Assert.Equal("protobuf parser span", span.Name);
        Assert.False(string.IsNullOrEmpty(span.StartTimeUnixNano));
        Assert.False(string.IsNullOrEmpty(span.EndTimeUnixNano));
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

    private static byte[] BuildMinimalProtobufTracePayload(string traceId, string spanId, string serviceName, string spanName)
    {
        var resourceAttribute = Message(w =>
        {
            w.WriteRawTag(10); w.WriteString("service.name");
            w.WriteRawTag(18); w.WriteBytes(Message(v => { v.WriteRawTag(10); v.WriteString(serviceName); }));
        });
        var resource = Message(w => { w.WriteRawTag(10); w.WriteBytes(resourceAttribute); });
        var span = Message(w =>
        {
            w.WriteRawTag(10); w.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(traceId)));
            w.WriteRawTag(18); w.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(spanId)));
            w.WriteRawTag(42); w.WriteString(spanName);
            w.WriteRawTag(48); w.WriteEnum(1);
            w.WriteRawTag(57); w.WriteFixed64(1767225600000000000UL);
            w.WriteRawTag(65); w.WriteFixed64(1767225601000000000UL);
        });
        var scopeSpans = Message(w => { w.WriteRawTag(18); w.WriteBytes(span); });
        var resourceSpans = Message(w =>
        {
            w.WriteRawTag(10); w.WriteBytes(resource);
            w.WriteRawTag(18); w.WriteBytes(scopeSpans);
        });
        return Message(w => { w.WriteRawTag(10); w.WriteBytes(resourceSpans); }).ToByteArray();
    }

    private static ByteString Message(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        write(output);
        output.Flush();
        return ByteString.CopyFrom(stream.ToArray());
    }

}
