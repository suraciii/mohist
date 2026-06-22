using System.Diagnostics;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs.Otel;

/// <summary>
/// Pinning tests for the exporter-self-feedback guard from design
/// Decision 4. The OTLP HTTP exporter's own HttpClient POST to the
/// configured OTLP endpoint MUST NOT appear as an outbound
/// <c>HttpClient</c> span in the captured pipeline — if it did, the
/// trace POST would itself produce a trace that triggers another
/// POST, an unbounded feedback loop.
///
/// <para>
/// The OTel SDK already wraps the exporter in
/// <c>SuppressInstrumentationScope</c>, which the contrib
/// <c>HttpClient</c> instrumentation honors. The unit tests below
/// also exercise the explicit <see cref="MohistOpenTelemetryRegistration.IsExporterSelfFeedback"/>
/// fallback so a regression in either layer is caught.
/// </para>
/// </summary>
[Collection("OtelTracing")]
public class OtelSelfFeedbackSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData("http://collector.test:4318/otel", "http://collector.test:4318/otel/v1/traces", true)]
    [InlineData("http://collector.test:4318/otel", "http://collector.test:4318/other", false)]
    [InlineData("http://collector.test:4318/otel", "http://other.test:4318/otel/v1/traces", false)]
    [InlineData("https://collector.test:443/otel", "https://collector.test:443/otel/v1/traces", true)]
    [InlineData("http://collector.test:4318/otel", "https://collector.test:443/otel/v1/traces", false)]
    [InlineData("http://collector.test:4318/otel/v1/traces", "http://collector.test:4318/otel/v1/traces", true)]
    [InlineData("http://collector.test:4318", "http://collector.test:4318/v1/traces", true)]
    public void IsExporterSelfFeedback_ReturnsTrue_ForRequestsMatchingExportEndpoint(
        string configuredBase, string requestUri, bool expected)
    {
        var exportEndpoint = MohistOpenTelemetryRegistration.ResolveExportEndpoint(configuredBase);
        var actual = MohistOpenTelemetryRegistration.IsExporterSelfFeedback(new Uri(requestUri), exportEndpoint);
        Assert.Equal(expected, actual);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task OtlpExporter_OutboundPost_ProducesNoOutboundHttpClientSpanToOtlpUri()
    {
        // Stand up an HttpListener receiver on a random free port —
        // this is the "OTLP collector" from the SDK's point of view.
        // Drive one inbound HTTP request through the host; the
        // inbound ASP.NET Core span propagates to the exporter, which
        // POSTs to the OTLP receiver. With the
        // FilterHttpRequestMessage fallback wired in
        // MohistOpenTelemetryRegistration.ConfigureTracing, the
        // exporter's outbound HttpClient call MUST NOT be captured
        // as an outbound span whose destination is the OTLP URI.
        //
        // The OtelTestHost mirrors the production source list (see
        // OtelTestHost.cs) including System.Net.Http /
        // OpenTelemetry.Instrumentation.Http. So any HttpClient
        // activity the SDK emits will appear in Recorder.
        using var receiver = new OtlpReceiver();
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            Endpoint = $"http://127.0.0.1:{receiver.Port}/otel",
        });

        using var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var response = await client.GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // Wait for the OTLP exporter to flush (BatchExportProcessor
        // flushes periodically). The receiver's first request IS the
        // exporter POST — that's the system under test producing its
        // own outbound span.
        var otlpRequest = await receiver.WaitForRequestAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(otlpRequest);
        Assert.Equal("POST", otlpRequest!.Method);

        // After the exporter POST lands, drain a beat so any
        // (forbidden) outbound HttpClient span from the POST itself
        // would have been captured by the recorder.
        await Task.Delay(500);

        var outboundToOtlp = host.Recorder.EndedActivities
            .Where(a => a.Source?.Name is "System.Net.Http" or "OpenTelemetry.Instrumentation.Http" or "OpenTelemetry.Instrumentation.Http.HttpClient")
            .Where(a => a.Kind == ActivityKind.Client)
            .Where(a =>
            {
                var url = a.GetTagItem("url.full") as string ?? a.GetTagItem("http.url") as string;
                return url is not null
                    && url.StartsWith($"http://127.0.0.1:{receiver.Port}", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        Assert.Empty(outboundToOtlp);
    }
}
