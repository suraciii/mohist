using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// In-process SignalR host with the production tracing registration.
/// The recorder is added to that same provider and never registers
/// sources or instrumentation of its own.
/// </summary>
public sealed class OtelTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TestServer _server;
    public RecordingActivityProcessor Recorder { get; }

    public OtelTestHost(
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        Recorder = new RecordingActivityProcessor();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        var otelBuilder = builder.Services.AddOpenTelemetry();
        MohistOpenTelemetryRegistration.ConfigureTracing(
            otelBuilder,
            new OtelOptions { Enabled = true, Endpoint = "http://collector.test/otel" },
            options => options.HttpClientFactory = () => new HttpClient(new SuccessResponseHandler()));
        otelBuilder.WithTracing(tracing => tracing.AddProcessor(Recorder));

        configureServices?.Invoke(builder.Services);

        _app = builder.Build();
        configureApp?.Invoke(_app);

        _server = _app.GetTestServer();
        _app.Start();
    }

    /// <summary>
    /// Exposes the <see cref="TestServer"/> so SignalR (and other
    /// long-lived client) tests can plug the in-process handler into
    /// their connection builder without spinning up a real socket.
    /// </summary>
    public TestServer TestServer => _server;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        _server.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Records activities emitted by the production tracing pipeline.
/// </summary>
public sealed class RecordingActivityProcessor : BaseProcessor<Activity>
{
    private readonly List<Activity> _ended = new();
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
        lock (_gate) _ended.Add(activity);
    }
}

internal sealed class SuccessResponseHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
            RequestMessage = request,
        });
    }
}
