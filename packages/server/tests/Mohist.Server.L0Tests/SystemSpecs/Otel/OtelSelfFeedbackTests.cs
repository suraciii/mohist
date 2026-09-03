using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.L0Tests.SystemSpecs.Otel;

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
/// <c>HttpClient</c> instrumentation honors. The tests below exercise
/// the explicit <see cref="MohistOpenTelemetryRegistration.IsExporterSelfFeedback"/>
/// fallback without posting to a real or fake collector.
/// </para>
/// </summary>
[Collection("OtelTracing")]
[Trait("level", "L0")]
public class OtelSelfFeedbackTests
{
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
}
