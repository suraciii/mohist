using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs.Otel;

/// <summary>
/// Pinning tests for outbound HttpClient instrumentation. The
/// standard <c>OpenTelemetry.Instrumentation.Http</c> package emits
/// one Client-kind activity per outbound <c>HttpClient</c> call;
/// each activity carries the destination URI and the response
/// status code.
///
/// <para>
/// These tests stand up a minimal HTTP receiver on a free port
/// (the <c>OtlpReceiver</c> type is reused — its contract is just
/// "capture one inbound HTTP request"), then route an outbound
/// <c>HttpClient</c> call through the production pipeline to it.
/// The <see cref="RecordingActivityProcessor"/> captures the
/// resulting outbound Client activity and the assertions pin the
/// destination URI and response status code.
/// </para>
/// </summary>
[Collection("OtelTracing")]
public class OtelOutboundHttpTracingSpecs : IDisposable
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
    public async Task OutboundHttpClient_Call_ProducesClientActivityWithDestinationUriAndStatus()
    {
        // Bind a tiny HTTP receiver on a free port. The receiver
        // mirrors what a self-update endpoint / readiness probe would
        // look like on the network.
        var receiver = new OtlpReceiver();
        _receivers.Add(receiver);
        var destination = $"http://127.0.0.1:{receiver.Port}/probe";

        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = $"http://127.0.0.1:{receiver.Port}/otel",
            ConfigureServices = services =>
            {
                // Register a typed HttpClient whose BaseAddress is the
                // local probe receiver. Any GET this client makes
                // hits our receiver and produces a Client-kind
                // outbound HttpClient activity.
                services.AddHttpClient("probe", client =>
                {
                    client.BaseAddress = new Uri(destination);
                });
            },
        });

        var httpClientFactory = host.Services
            .GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("probe");

        // Send a request whose response includes the path /probe
        // and a 200 OK status — the receiver's recorded request is
        // captured below for path/method assertions, and the
        // activity's tags carry the same URI + status.
        var response = await client.GetAsync("/probe");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Wait for the inbound request to land on the receiver (the
        // outbound call we just made).
        var landed = await receiver.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(landed);
        Assert.Equal("/probe", landed!.Path);
        Assert.Equal("GET", landed.Method);

        // The instrumentation source for outbound HttpClient calls
        // is the System.Net.Http source (the BCL-internal
        // HttpClient-instrumentation source). The recorded
        // activity MUST be Client-kind and MUST carry the
        // destination URI + status.
        var outbound = host.Recorder.EndedActivities
            .Where(a => a.Kind == ActivityKind.Client
                && (a.Source?.Name is "System.Net.Http"
                    or "OpenTelemetry.Instrumentation.Http"
                    or "OpenTelemetry.Instrumentation.Http.HttpClient"))
            .Where(a => (a.GetTagItem("url.full") as string ?? a.GetTagItem("http.url") as string)
                ?.StartsWith(destination, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.NotEmpty(outbound);
        var activity = outbound[0];
        // http.response.status_code is the standard semantic
        // convention tag emitted by the HttpClient instrumentation
        // package.
        Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
    }
}