using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Mohist.Server.TestSupport;

/// <summary>
/// A minimal in-process OpenTelemetry integration host. Builds a
/// <see cref="WebApplication"/> with the production
/// <see cref="MohistOpenTelemetryRegistration.AddMohistOpenTelemetry"/>
/// wiring plus one mapped inbound route + one route under
/// <c>/otel/</c> for the filter-exclusion scenario, runs it on
/// <see cref="TestServer"/>, and captures every Activity the OTel
/// pipeline emits on a <see cref="RecordingActivityProcessor"/>.
///
/// <para>
/// This class deliberately avoids <c>WebApplicationFactory</c> so the
/// OTel registration can be driven through the hosted pipeline without an extra
/// production <c>Program</c> entry point, and so each test owns a
/// fresh <see cref="IHost"/> (and a fresh
/// <see cref="OpenTelemetryBuilder"/> / <c>TracerProvider</c>) — the
/// master-switch off-state and the endpoint-unreachable scenarios
/// both need isolated hosts.
/// </para>
///
/// <para>
/// The host registers the production OpenTelemetry pipeline (which
/// subscribes to all five automatic instrumentation sources) AND a
/// second in-process <see cref="TracerProvider"/> that subscribes to
/// the same sources and feeds a <see cref="RecordingActivityProcessor"/>.
/// Two separate providers is the canonical way to verify what a
/// pipeline actually emits: the production provider's OTLP exporter
/// delivers spans to the network, while the test provider's
/// <see cref="RecordingActivityProcessor"/> records them in-process
/// for assertions. The two providers share process-global
/// <see cref="ActivitySource"/> state, so the test provider sees every
/// activity the production provider sees.
/// </para>
/// </summary>
public sealed class OtelTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TestServer _server;
    private readonly RecordingHttpMessageHandler _otlpExporterHandler;

    public RecordingActivityProcessor Recorder { get; }
    public IReadOnlyList<HttpRequestMessage> OtlpExporterRequests => _otlpExporterHandler.Requests;
    public bool FakeExporterConfigured { get; private set; }

    public OtelTestHost(OtelTestHostOptions options)
    {
        Recorder = new RecordingActivityProcessor();
        _otlpExporterHandler = new RecordingHttpMessageHandler(options.FailExporterRequests);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(
            builder.Configuration.GetValue("Logging:LogLevel:Default", LogLevel.Warning));
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(options.AsConfiguration());

        builder.Services.AddRouting();
        builder.Services.Configure<OtelOptions>(builder.Configuration.GetSection(OtelOptions.SectionName));
        var otelOptions = builder.Configuration.GetSection(OtelOptions.SectionName).Get<OtelOptions>() ?? new OtelOptions();
        if (otelOptions.Enabled)
        {
            MohistOpenTelemetryRegistration.ConfigureTelemetry(
                builder.Services.AddOpenTelemetry(),
                otelOptions,
                ConfigureFakeExporter);
        }

        if (options.Enabled)
        {
            // Mirror every source the production SDK subscribes to so
            // this in-process provider is a listener for the same
            // spans. The production provider is responsible for the
            // real OTLP export; this provider is the test capture
            // channel only.
            builder.Services.AddOpenTelemetry().WithTracing(tracing =>
            {
                tracing.AddSource("Microsoft.AspNetCore");
                foreach (var sourceName in MohistOpenTelemetryRegistration.OrleansActivitySourceNames)
                {
                    tracing.AddSource(sourceName);
                }
                tracing
                    .AddSource("OpenTelemetry.Instrumentation.EntityFrameworkCore")
                    .AddSource("System.Net.Http")
                    .AddSource("OpenTelemetry.Instrumentation.Http.HttpClient")
                    .AddProcessor(Recorder);
            });
        }

        options.ConfigureServices?.Invoke(builder.Services);

        _app = builder.Build();
        _app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        _app.MapPost("/otel/v1/traces", () => Results.Ok());

        // Optional custom route mapping (used by the single-trace
        // continuity integration tests).
        options.ConfigureApp?.Invoke(_app);

        _server = _app.GetTestServer();
        _app.Start();
    }

    public bool ForceFlushOtelExporter(TimeSpan timeout)
    {
        var provider = _app.Services.GetRequiredService<TracerProvider>();
        return provider.ForceFlush((int)timeout.TotalMilliseconds);
    }

    private void ConfigureFakeExporter(OtlpExporterOptions options)
    {
        FakeExporterConfigured = true;
        options.ExportProcessorType = ExportProcessorType.Simple;
        options.HttpClientFactory = () => new HttpClient(_otlpExporterHandler, disposeHandler: false);
    }

    public HttpClient CreateClient() => _server.CreateClient();

    /// <summary>
    /// Exposes the <see cref="TestServer"/> so long-lived client tests can
    /// plug the in-process handler into their connection builder without
    /// spinning up a real socket.
    /// </summary>
    public TestServer TestServer => _server;

    public IServiceProvider Services => _app.Services;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        _server.Dispose();
        await _app.DisposeAsync();
    }
}

public sealed class OtelTestHostOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExportEnabled { get; init; } = true;
    public string? Endpoint { get; init; }
    public bool FailExporterRequests { get; init; }

    /// <summary>
    /// Optional hook to register additional services BEFORE
    /// <see cref="WebApplication.Build"/> runs. Used by tests that
    /// need extra middleware services that must be wired before any
    /// endpoint is mapped.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// Optional hook to map additional routes
    /// after the default <c>/api/health</c> and <c>/otel/v1/traces</c>
    /// routes have been mapped. Used by integration tests that need
    /// to exercise an EF / outbound-HttpClient chain through the hosted
    /// pipeline.
    /// </summary>
    public Action<WebApplication>? ConfigureApp { get; init; }

    public IEnumerable<KeyValuePair<string, string?>> AsConfiguration()
    {
        yield return new KeyValuePair<string, string?>("Mohist:Otel:Enabled", Enabled ? "true" : "false");
        yield return new KeyValuePair<string, string?>("Mohist:Otel:ExportEnabled", ExportEnabled ? "true" : "false");
        if (Endpoint is not null)
        {
            yield return new KeyValuePair<string, string?>("Mohist:Otel:Endpoint", Endpoint);
        }
    }
}

/// <summary>
/// Minimal <see cref="BaseProcessor{T}"/> that records every
/// <see cref="Activity"/> the provider's pipeline emits on
/// <c>OnEnd</c>. Exposed via <see cref="OtelTestHost.Recorder"/>.
/// </summary>
public sealed class RecordingActivityProcessor : BaseProcessor<Activity>
{
    private readonly List<Activity> _ended = new();
    private readonly List<PendingWait> _waiters = new();
    private readonly object _gate = new();

    /// <summary>
    /// A point-in-time snapshot of every ended activity captured so far.
    /// Returns a copy so callers iterate a stable view even while
    /// <see cref="OnEnd"/> keeps appending.
    /// </summary>
    public IReadOnlyList<Activity> EndedActivities
    {
        get
        {
            lock (_gate) return _ended.ToList();
        }
    }

    public override void OnEnd(Activity activity)
    {
        List<Activity>? snapshot = null;
        lock (_gate)
        {
            _ended.Add(activity);
            if (_waiters.Count > 0)
            {
                snapshot = _ended.ToList();
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    var wait = _waiters[i];
                    if (wait.Predicate(snapshot))
                    {
                        _waiters.RemoveAt(i);
                        wait.Tcs.TrySetResult(snapshot);
                    }
                }
            }
        }
    }

    public Task<List<Activity>> WaitForAsync(
        Func<List<Activity>, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var initial = _ended.ToList();
            if (predicate(initial))
                return Task.FromResult(initial);

            var tcs = new TaskCompletionSource<List<Activity>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = new PendingWait(predicate, tcs);
            _waiters.Add(wait);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    lock (_gate) _waiters.Remove(wait);
                    tcs.TrySetCanceled(cancellationToken);
                });
            }
            return tcs.Task;
        }
    }

    private sealed class PendingWait
    {
        public Func<List<Activity>, bool> Predicate { get; }
        public TaskCompletionSource<List<Activity>> Tcs { get; }
        public PendingWait(Func<List<Activity>, bool> predicate, TaskCompletionSource<List<Activity>> tcs)
        {
            Predicate = predicate;
            Tcs = tcs;
        }
    }
}

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = new();
    private readonly bool _failRequests;

    public RecordingHttpMessageHandler(bool failRequests)
    {
        _failRequests = failRequests;
    }

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);
        if (_failRequests)
        {
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Fake OTLP exporter transport failure."));
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
            RequestMessage = request,
        });
    }
}
