using System.Diagnostics;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Pinning tests for the outbound HttpClient activity source. The
/// production registration subscribes to the BCL <c>System.Net.Http</c>
/// source so Client-kind outbound work can be captured alongside
/// Mohist's server-side trace.
///
/// <para>
/// The test emits a synthetic <c>System.Net.Http</c> activity instead
/// of creating a real <c>HttpClient</c> call. That keeps the contract
/// under test at the Mohist registration boundary and avoids depending
/// on OS sockets or external endpoints.
/// </para>
/// </summary>
[Collection("OtelTracing")]
public class OtelOutboundHttpTracingSpecs
{
    [Fact]
    public async Task SystemNetHttpClientActivitySource_IsCapturedWithDestinationUriAndStatus()
    {
        const string destination = "http://probe.test/probe";

        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = "http://collector.test/otel",
        });

        using var httpClientSource = new ActivitySource("System.Net.Http");
        using (var emitted = httpClientSource.StartActivity("GET", ActivityKind.Client))
        {
            emitted?.SetTag("url.full", destination);
            emitted?.SetTag("http.response.status_code", 200);
        }

        var outbound = host.Recorder.EndedActivities
            .Where(a => a.Kind == ActivityKind.Client && a.Source?.Name == "System.Net.Http")
            .Where(a => (a.GetTagItem("url.full") as string ?? a.GetTagItem("http.url") as string)
                ?.StartsWith(destination, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.NotEmpty(outbound);
        var activity = outbound[0];
        Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
    }
}
