using System.Diagnostics;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.SystemSpecs.Otel;

[Collection("OtelTracing")]
[Trait("level", "L1")]
public sealed class OtelHostExporterActivitySpecs
{
    [Fact]
    public async Task SpecHostExporterDisabled_KeepsInstrumentationWithoutExporterRequests()
    {
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            ExportEnabled = false,
        });

        using var response = await host.CreateClient().GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var source = new ActivitySource(MohistOpenTelemetryRegistration.OrleansActivitySourceNames[0]);
        using (var activity = source.StartActivity("export-probe", ActivityKind.Internal))
        {
            Assert.NotNull(activity);
        }
        Assert.True(host.ForceFlushOtelExporter(TimeSpan.FromSeconds(1)));
        Assert.False(host.FakeExporterConfigured);
        Assert.Empty(host.OtlpExporterRequests);
    }
}
