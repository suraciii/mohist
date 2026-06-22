using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Mohist.Server.Tests.Support;

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
/// OTel registration can be driven end-to-end without an extra
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

    public RecordingActivityProcessor Recorder { get; }

    public OtelTestHost(OtelTestHostOptions options)
    {
        Recorder = new RecordingActivityProcessor();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(options.AsConfiguration());

        builder.Services.AddRouting();
        builder.Services.AddMohistOpenTelemetry(builder.Configuration);

        if (options.Enabled)
        {
            // Mirror every source the production SDK subscribes to so
            // this in-process provider is a listener for the same
            // spans. The production provider is responsible for the
            // real OTLP export; this provider is the test capture
            // channel only.
            builder.Services.AddOpenTelemetry().WithTracing(tracing =>
            {
                tracing
                    .AddSource("Microsoft.AspNetCore")
                    .AddSource(MohistOpenTelemetryRegistration.SignalRServerActivitySourceName);
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

    public HttpClient CreateClient() => _server.CreateClient();

    /// <summary>
    /// Exposes the <see cref="TestServer"/> so SignalR (and other
    /// long-lived client) tests can plug the in-process handler into
    /// their connection builder without spinning up a real socket.
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
    public string? Endpoint { get; init; }

    /// <summary>
    /// Optional hook to register additional services BEFORE
    /// <see cref="WebApplication.Build"/> runs. Used by tests that
    /// need extra middleware services (e.g. SignalR via
    /// <c>AddSignalR</c>) that must be wired before any endpoint is
    /// mapped.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// Optional hook to map additional routes / SignalR hubs / etc.
    /// after the default <c>/api/health</c> and <c>/otel/v1/traces</c>
    /// routes have been mapped. Used by integration tests that need
    /// to exercise a SignalR hub method or an EF / outbound-HttpClient
    /// chain end-to-end.
    /// </summary>
    public Action<WebApplication>? ConfigureApp { get; init; }

    public IEnumerable<KeyValuePair<string, string?>> AsConfiguration()
    {
        yield return new KeyValuePair<string, string?>("Mohist:Otel:Enabled", Enabled ? "true" : "false");
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

    public IReadOnlyList<Activity> EndedActivities => _ended;

    public override void OnEnd(Activity activity) => _ended.Add(activity);
}