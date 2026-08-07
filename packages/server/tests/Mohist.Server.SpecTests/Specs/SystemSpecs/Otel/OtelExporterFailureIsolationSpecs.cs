using System.Net;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Verifies the host remains healthy when the OTLP exporter is wired
/// to a failing fake transport. The test does not bind a socket or
/// connect to an external collector.
/// </summary>
[Collection("OtelTracing")]
public class OtelExporterFailureIsolationSpecs
{
    [Fact]
    public async Task ServerStarts_AndServesRequest_WithFailingFakeExporterTransport()
    {
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = "http://collector.test/otel",
            FailExporterRequests = true,
        });

        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
