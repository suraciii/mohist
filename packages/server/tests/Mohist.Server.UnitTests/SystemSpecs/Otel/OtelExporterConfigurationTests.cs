using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs.Otel;

[Collection("OtelTracing")]
public class OtelExporterConfigurationTests
{
    [Fact]
    public void ResolveExportEndpoint_AppendsV1TracesToBase()
    {
        var endpoint = MohistOpenTelemetryRegistration.ResolveExportEndpoint("http://collector.example/otel");

        Assert.Equal(new Uri("http://collector.example/otel/v1/traces"), endpoint);
    }

    [Theory]
    [InlineData("http://localhost:4318/otel", "http://localhost:4318/otel/v1/metrics")]
    [InlineData("http://localhost:4318", "http://localhost:4318/v1/metrics")]
    [InlineData("https://otel.example.com:443/otel", "https://otel.example.com:443/otel/v1/metrics")]
    [InlineData("http://10.0.0.1:4318/some/nested/path", "http://10.0.0.1:4318/some/nested/path/v1/metrics")]
    public void ResolveMetricsExportEndpoint_AppendsV1MetricsToBase(string baseEndpoint, string expected)
    {
        var endpoint = MohistOpenTelemetryRegistration.ResolveMetricsExportEndpoint(baseEndpoint);

        Assert.Equal(new Uri(expected), endpoint);
    }
}
