using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using EnvironmentAbstractions;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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
    public async Task RealSdkHttpProtobufExport_PersistsToOtelDb()
    {
        var otlpPort = GetFreePort();
        var otlpDbPath = Path.Combine(Path.GetTempPath(), $"mohist-real-sdk-otel-{Guid.NewGuid():N}.db");
        await using var app = BuildRealSocketOtlpApp(otlpPort, otlpDbPath);
        await app.StartAsync();

        using var source = new ActivitySource("Mohist.Server.Tests.RealOtlpExport");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(resource => resource.AddService("real-sdk-export"))
            .AddSource(source.Name)
            .AddOtlpExporter(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = MohistOpenTelemetryRegistration.ResolveExportEndpoint($"http://127.0.0.1:{otlpPort}/otel");
            })
            .Build();

        using (var activity = source.StartActivity("real sdk protobuf span", ActivityKind.Internal))
        {
            activity?.SetTag("test.marker", "real-sdk-protobuf");
        }

        Assert.True(provider.ForceFlush(10_000));

        var db = app.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesServiceNameColumn} = $service";
        cmd.Parameters.AddWithValue("$service", "real-sdk-export");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("real-sdk-export", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));

        await app.StopAsync();
        try { if (File.Exists(otlpDbPath)) File.Delete(otlpDbPath); } catch { }
    }

    [Fact]
    public async Task RealSdkHttpProtobufPayload_ParsesRequiredSpanFields()
    {
        using var receiver = new Mohist.Server.Tests.Specs.SystemSpecs.Otel.OtlpReceiver();
        using var source = new ActivitySource("Mohist.Server.Tests.RealOtlpParse");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(resource => resource.AddService("real-sdk-parse"))
            .AddSource(source.Name)
            .AddOtlpExporter(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = MohistOpenTelemetryRegistration.ResolveExportEndpoint($"http://127.0.0.1:{receiver.Port}/otel");
            })
            .Build();

        using (source.StartActivity("real sdk parser span", ActivityKind.Internal))
        {
        }

        Assert.True(provider.ForceFlush(10_000));
        var request = await receiver.WaitForRequestAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(request);

        var parsed = Mohist.Server.Otel.OtlpProtobuf.OtlpProtobufTraceParser.Parse(request!.Body);
        var resourceSpans = Assert.Single(parsed.ResourceSpans ?? []);
        var scopeSpans = Assert.Single(resourceSpans.ScopeSpans ?? []);
        var span = Assert.Single(scopeSpans.Spans ?? []);
        Assert.False(string.IsNullOrEmpty(span.TraceId));
        Assert.False(string.IsNullOrEmpty(span.SpanId));
        Assert.Equal("real sdk parser span", span.Name);
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

    private static WebApplication BuildRealSocketOtlpApp(int otlpPort, string otlpDbPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(otlpPort));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:Otel:Enabled"] = "true",
            ["Mohist:Otel:Port"] = otlpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mohist:Otel:DbPath"] = otlpDbPath,
        });
        builder.Services.Configure<OtelOptions>(builder.Configuration.GetSection(OtelOptions.SectionName));
        builder.Services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        builder.Services.AddSingleton<IEnvironmentVariableProvider>(SystemEnvironmentVariableProvider.Instance);
        builder.Services.AddSingleton<OtelDb>();
        builder.Services.AddSingleton<TraceIngester>();

        var app = builder.Build();
        app.MapOtlpRoutes();
        return app;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
