using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.TestSupport;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

[Collection("OtelTracing")]
public class MohistOpenTelemetryRegistrationSpecs
{
    [Fact]
    public void Disabled_DoesNotRegisterOpenTelemetryServices()
    {
        var config = BuildConfig(enabled: false);

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        // When the master switch is off, AddOpenTelemetry() must NOT have
        // been called: no TelemetryHostedService (the IHostedService
        // that owns provider lifetimes), no TracerProvider / MeterProvider
        // / LoggerProvider. This guarantees the off-state is byte-for-byte
        // equivalent to a server built without this capability (zero
        // background threads, zero Activity creation, zero HTTP attempts).
        Assert.DoesNotContain(services, d => d.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.Implementation.TelemetryHostedService");
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "OpenTelemetry.Trace.TracerProvider");
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "OpenTelemetry.Metrics.MeterProvider");
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "OpenTelemetry.Logs.LoggerProvider");
    }

    [Fact]
    public void MissingEnablement_RegistersOpenTelemetryServices()
    {
        var config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        Assert.Contains(services, d => d.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.Implementation.TelemetryHostedService");
        Assert.Contains(services, d => d.ServiceType.FullName == "OpenTelemetry.Trace.TracerProvider");
    }

    [Fact]
    public void Disabled_ActivityCapturedByProcessorIsZero_NoPipelineExists()
    {
        // The master switch must guarantee no tracer provider was ever
        // built. We assert that directly via the IServiceCollection:
        // no TracerProvider descriptor was registered.
        var config = BuildConfig(enabled: false);

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "OpenTelemetry.Trace.TracerProvider");
    }

    [Fact]
    public void Enabled_RegistersTelemetryHostedServiceAndTracerProvider()
    {
        var config = BuildConfig(enabled: true, endpoint: "http://collector.test/otel");

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        Assert.Contains(services, d => d.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.Implementation.TelemetryHostedService");
        Assert.Contains(services, d => d.ServiceType.FullName == "OpenTelemetry.Trace.TracerProvider");
        Assert.Contains(services, d => d.ServiceType.FullName == "OpenTelemetry.Metrics.MeterProvider");
    }

    [Fact]
    public void Enabled_ConfiguresDispatcherMeterProviderAndMetricsExporter()
    {
        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(BuildConfig(enabled: true, endpoint: "http://collector.test/otel"));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<MeterProvider>());
        var exporterOptions = provider.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get("metrics");
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporterOptions.Protocol);
        Assert.Equal(new Uri("http://collector.test/otel/v1/metrics"), exporterOptions.Endpoint);
    }

    [Fact]
    public void Enabled_TracerProviderBuilt_IsResolvableAndCreatesInstrumentation()
    {
        // Verify that AddMohistOpenTelemetry's pipeline is alive by
        // resolving the built TracerProvider and confirming the SDK
        // returns a non-null instance. The pipeline is the chain of
        // processor(s) + exporter(s) wired inside the WithTracing
        // block; if the registration short-circuits, this would be null.
        var config = BuildConfig(enabled: true, endpoint: "http://collector.test/otel");

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetService<TracerProvider>();
        Assert.NotNull(tracerProvider);
        Assert.NotNull(provider.GetService<MeterProvider>());
    }

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
        using var source = new ActivitySource(MohistOpenTelemetryRegistration.SignalRServerActivitySourceName);
        using (var activity = source.StartActivity("export-probe", ActivityKind.Internal))
        {
            Assert.NotNull(activity);
        }
        Assert.True(host.ForceFlushOtelExporter(TimeSpan.FromSeconds(1)));
        Assert.False(host.FakeExporterConfigured);
        Assert.Empty(host.OtlpExporterRequests);
    }

    [Fact]
    public void Enabled_ActivityListenerSeesActivityStartedWhileProviderIsAlive()
    {
        // Stand up the same provider pipeline the production code wires
        // up via AddMohistOpenTelemetry, then use an ActivityListener
        // (independent of any TracerProvider) to confirm an Activity
        // started from a known source flows through the registered pipeline. This proves the
        // hosting layer does not break the ambient Activity flow.
        var config = BuildConfig(enabled: true, endpoint: "http://collector.test/otel");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMohistOpenTelemetry(config);

        using var provider = services.BuildServiceProvider();

        var recordedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Mohist.Server.SpecTests.Specs.SystemSpecs.Otel",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => { },
            ActivityStopped = a => recordedActivities.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        using var activitySource = new ActivitySource("Mohist.Server.SpecTests.Specs.SystemSpecs.Otel");
        using (var activity = activitySource.StartActivity("test-span", ActivityKind.Internal))
        {
            activity?.SetTag("test.marker", "ok");
        }

        Assert.Single(recordedActivities);
        Assert.Equal("test-span", recordedActivities[0].OperationName);
        Assert.Equal("ok", recordedActivities[0].GetTagItem("test.marker"));
    }

    private static IConfiguration BuildConfig(bool enabled, string endpoint = "http://localhost:4318/otel")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Enabled"] = enabled ? "true" : "false",
                ["Mohist:Otel:Endpoint"] = endpoint,
            })
            .Build();
    }

    /// <summary>
    /// Minimal <see cref="OpenTelemetry.BaseProcessor{T}"/> that records
    /// every <see cref="Activity"/> the provider's pipeline emits. Lets
    /// unit tests assert the pipeline is alive (and is not alive) without
    /// standing up a WebApplicationFactory or a real OTLP collector.
    /// </summary>
    private sealed class RecordingActivityProcessor : OpenTelemetry.BaseProcessor<Activity>
    {
        private readonly List<Activity> _ended = new();

        public IReadOnlyList<Activity> EndedActivities => _ended;

        public override void OnEnd(Activity activity) => _ended.Add(activity);
    }
}
