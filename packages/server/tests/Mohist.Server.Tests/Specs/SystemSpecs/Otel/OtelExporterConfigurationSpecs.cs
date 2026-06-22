using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Tests.Support;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs.Otel;

/// <summary>
/// Integration tests that verify the OTLP exporter is configured with
/// the documented protocol (HTTP/Protobuf) and endpoint, and that the
/// export pipeline actually delivers spans to that endpoint.
///
/// These tests stand up a tiny <see cref="HttpListener"/>-based OTLP
/// receiver on a free localhost port and bind
/// <see cref="MohistOpenTelemetryRegistration.AddMohistOpenTelemetry"/>
/// to it. They are kept distinct from
/// <see cref="OtelInboundHttpTracingSpecs"/> because they exercise a
/// different code path (the outbound OTLP HTTP POST) and need an
/// actual HTTP socket on the loopback adapter.
/// </summary>
[Collection("OtelTracing")]
public class OtelExporterConfigurationSpecs : IDisposable
{
    private readonly List<OtlpReceiver> _receivers = new();

    public void Dispose()
    {
        foreach (var r in _receivers)
        {
            r.Dispose();
        }
        _receivers.Clear();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task OtlpExporter_PostsTracesToConfiguredEndpointOverHttpProtobuf()
    {
        // Stand up a tiny HTTP receiver on a random free port that
        // records the inbound OTLP POST. This stands in for the
        // #219 same-process collector so we can assert both that the
        // SDK POSTs to the configured endpoint and that the
        // destination URI matches what OtelOptions carries.
        var receiver = new OtlpReceiver();
        _receivers.Add(receiver);
        var endpoint = $"http://127.0.0.1:{receiver.Port}/otel";

        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = endpoint,
        });
        using var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The OTLP exporter is asynchronous (BatchExportProcessor).
        // Wait up to 5 s for the POST to land.
        var received = await receiver.WaitForRequestAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(received);
        Assert.Equal("POST", received!.Method);
        // The OTLP HTTP exporter appends /v1/traces to the configured
        // base endpoint per spec. With endpoint http://.../otel the
        // POST should land at /otel/v1/traces, matching #219's ingest
        // contract.
        Assert.Equal("/otel/v1/traces", received.Path);
        // The OTel .NET SDK ships with content-type "application/x-protobuf"
        // (see OtlpHttpExportClient.MediaHeaderValue). Accept either
        // spelling — they are semantically the same OTLP framing.
        Assert.NotNull(received.ContentType);
        Assert.True(
            received.ContentType!.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase) ||
            received.ContentType!.StartsWith("application/protobuf", StringComparison.OrdinalIgnoreCase),
            $"Expected protobuf content-type, got '{received.ContentType}'.");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void ConfigureTracing_OtlpExporterEndpoint_AppendsV1TracesToBase()
    {
        // The trace-specific AddOtlpExporter(Action<OtlpExporterOptions>)
        // overload applies the inline configure delegate when the SDK
        // builds the exporter — it does NOT register the delegate via
        // services.Configure. To make the configured values inspectable
        // for tests, AddMohistOpenTelemetry ALSO registers the same
        // configuration via services.Configure<OtlpExporterOptions>.
        // Resolving IOptions<OtlpExporterOptions> then yields the
        // configured protocol + endpoint so the test can assert what
        // the export pipeline will receive.
        //
        // /v1/traces is appended to the base URL because the SDK only
        // auto-appends when the endpoint comes from spec env vars; the
        // explicit-set path requires us to do it.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Enabled"] = "true",
                ["Mohist:Otel:Endpoint"] = "http://collector.example/otel",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        using var provider = services.BuildServiceProvider();
        var optionsAccessor = provider.GetRequiredService<IOptions<OtlpExporterOptions>>();

        Assert.Equal(OtlpExportProtocol.HttpProtobuf, optionsAccessor.Value.Protocol);
        Assert.Equal(new Uri("http://collector.example/otel/v1/traces"), optionsAccessor.Value.Endpoint);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void ConfigureTracing_OtlpExporterEndpoint_ReflectsConfiguredOtelOptions()
    {
        // Same as above, but parameterized on the configured endpoint
        // so we can flip through several hostnames and ports and
        // confirm every one is forwarded verbatim with /v1/traces
        // appended. This is the production-config-to-options bridge:
        // changing Mohist:Otel:Endpoint in config (or via
        // MOHIST__Otel__Endpoint) must propagate to the exporter.
        foreach (var (baseEndpoint, expected) in new[]
                 {
                     ("http://localhost:4318/otel", "http://localhost:4318/otel/v1/traces"),
                     ("http://localhost:4318", "http://localhost:4318/v1/traces"),
                     ("https://otel.example.com:443/otel", "https://otel.example.com:443/otel/v1/traces"),
                     ("http://10.0.0.1:4318/some/nested/path", "http://10.0.0.1:4318/some/nested/path/v1/traces"),
                 })
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mohist:Otel:Enabled"] = "true",
                    ["Mohist:Otel:Endpoint"] = baseEndpoint,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddMohistOpenTelemetry(config);

            using var provider = services.BuildServiceProvider();
            var optionsAccessor = provider.GetRequiredService<IOptions<OtlpExporterOptions>>();

            Assert.Equal(OtlpExportProtocol.HttpProtobuf, optionsAccessor.Value.Protocol);
            Assert.Equal(new Uri(expected), optionsAccessor.Value.Endpoint);
        }
    }
}

/// <summary>
/// Lightweight HTTP listener bound to 127.0.0.1 on an OS-assigned
/// free port. Captures the first inbound request — method, path, and
/// content-type — so tests can assert the OTLP exporter's outbound
/// destination and payload framing without standing up a full ASP.NET
/// pipeline.
/// </summary>
internal sealed class OtlpReceiver : IDisposable
{
    private readonly System.Net.HttpListener _listener;
    private readonly TaskCompletionSource<OtlpRequest> _received = new();
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public OtlpReceiver()
    {
        Port = GetFreePort();
        _listener = new System.Net.HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public async Task<OtlpRequest?> WaitForRequestAsync(TimeSpan timeout)
    {
        var winner = await Task.WhenAny(_received.Task, Task.Delay(timeout));
        if (winner != _received.Task) return null;
        return await _received.Task;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                var contentType = ctx.Request.ContentType;
                var path = ctx.Request.Url?.AbsolutePath;
                var method = ctx.Request.HttpMethod;

                using var memory = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(memory);
                _received.TrySetResult(new OtlpRequest(method ?? "POST", path ?? "/", contentType, memory.ToArray()));
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

internal sealed record OtlpRequest(string Method, string Path, string? ContentType, byte[] Body);
