using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs.Otel;

/// <summary>
/// Verifies the spec contract that exporter failure is non-fatal: a
/// connection-refused OTLP endpoint MUST NOT throw exceptions into
/// the request path, MUST NOT block request handling, and MUST NOT
/// crash the host. The OTel SDK's <c>BatchExportProcessor</c>
/// delivers this property by design (design Decision 5); this test
/// pins the contract so a future regression in our registration —
/// for instance, an accidental <c>try/catch</c> that rethrows — is
/// caught.
/// </summary>
[Collection("OtelTracing")]
public class OtelExporterFailureIsolationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task ServerStarts_AndServesRequest_WhenExporterEndpointIsRefused()
    {
        // Pick a free port and IMMEDIATELY release it; by the time
        // the host's first export attempt lands, the OS will reply
        // with connection-refused (or, if a peer raced to bind, with
        // RST). Either way the export attempt fails and the SDK must
        // not propagate the failure into the request path.
        var deadPort = GetFreePort();

        var startedAt = DateTime.UtcNow;
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = $"http://127.0.0.1:{deadPort}/otel",
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
        client.Timeout = TimeSpan.FromSeconds(15);

        // The actual exporter failure path: send a request that
        // produces an ASP.NET Core inbound span and let the SDK try
        // (and fail) to export. With a refused endpoint, the
        // BatchExportProcessor retries in the background — the
        // request itself must return within the client's timeout
        // (15 s) regardless of the exporter's own state.
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

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
