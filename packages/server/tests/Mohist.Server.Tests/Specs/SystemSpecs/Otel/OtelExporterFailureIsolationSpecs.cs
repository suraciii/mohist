using System.Net;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs.Otel;

/// <summary>
/// Verifies the host remains healthy when the OTLP exporter is wired
/// to a failing fake transport. The test does not bind a socket or
/// connect to an external collector.
/// </summary>
[Collection("OtelTracing")]
public class OtelExporterFailureIsolationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task ServerStarts_AndServesRequest_WithFailingFakeExporterTransport()
    {
        var startedAt = DateTime.UtcNow;
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = "http://collector.test/otel",
            FailExporterRequests = true,
        });
        var hostReadyAt = DateTime.UtcNow;

        // Host boot must complete without throwing. AddMohistOpenTelemetry
        // registers the exporter lazily — actual export is triggered by
        // Activity end, which happens on the request path, not at
        // host boot. We don't measure startup time here; the real
        // assertion is that the host's Build/Start didn't fail.
        Assert.True((hostReadyAt - startedAt).TotalSeconds < 10,
            $"Host startup took {(hostReadyAt - startedAt).TotalSeconds:F1} s; expected under 10 s.");

        using var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var requestStartedAt = DateTime.UtcNow;
        var response = await client.GetAsync("/api/health");
        var requestFinishedAt = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var elapsed = requestFinishedAt - requestStartedAt;
        // Belt-and-suspenders: a healthy request on the in-process
        // TestServer typically completes in <1 s. We use a 10 s
        // ceiling — generous enough to absorb CI jitter, tight
        // enough to fail if the exporter is allowed to block the
        // request thread.
        Assert.True(elapsed.TotalSeconds < 10,
            $"Request took {elapsed.TotalSeconds:F1} s; exporter appears to be blocking the request path.");
    }
}
