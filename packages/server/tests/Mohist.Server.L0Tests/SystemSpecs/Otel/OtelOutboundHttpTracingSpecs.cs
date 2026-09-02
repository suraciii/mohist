using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Mohist.Server.L0Tests.SystemSpecs.Otel;

public sealed class OtelOutboundHttpTracingSpecs
{
    [Fact]
    public void SystemNetHttpClientActivitySource_IsCapturedWithDestinationUriAndStatus()
    {
        const string destination = "http://probe.test/probe";
        var services = new ServiceCollection();
        var recorded = new List<Activity>();
        var builder = services.AddOpenTelemetry();
        MohistOpenTelemetryRegistration.ConfigureTelemetry(
            builder,
            new OtelOptions
            {
                Enabled = true,
                ExportEnabled = false,
                Endpoint = "http://collector.test/otel",
            });
        builder.WithTracing(tracing => tracing.AddProcessor(new RecordingActivityProcessor(recorded)));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();

        using var source = new ActivitySource("System.Net.Http");
        using (var activity = source.StartActivity("GET", ActivityKind.Client))
        {
            Assert.NotNull(activity);
            activity!.SetTag("url.full", destination);
            activity.SetTag("http.response.status_code", 200);
        }

        var outbound = recorded
            .Where(activity => activity.Kind == ActivityKind.Client && activity.Source?.Name == "System.Net.Http")
            .Where(activity => (activity.GetTagItem("url.full") as string ?? activity.GetTagItem("http.url") as string)
                ?.StartsWith(destination, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.NotEmpty(outbound);
        Assert.Equal(200, outbound[0].GetTagItem("http.response.status_code"));
    }

    private sealed class RecordingActivityProcessor(ICollection<Activity> ended) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => ended.Add(activity);
    }
}
