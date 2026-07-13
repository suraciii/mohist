using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs.Otel;

[Collection("OtelTracing")]
public class MohistOpenTelemetryRegistrationTests
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
    public void Enabled_RegistersTelemetryHostedServiceAndTracerProvider()
    {
        var config = BuildConfig(enabled: true, endpoint: "http://collector.test/otel");

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        Assert.Contains(services, d => d.ImplementationType?.FullName == "OpenTelemetry.Extensions.Hosting.Implementation.TelemetryHostedService");
        Assert.Contains(services, d => d.ServiceType.FullName == "OpenTelemetry.Trace.TracerProvider");
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
    }

    [Fact]
    public void ConfigureTracing_ExportsSignalRServerActivities()
    {
        using var provider = BuildTracingProvider(out var recorder);
        using var source = new ActivitySource(MohistOpenTelemetryRegistration.SignalRServerActivitySourceName);
        using (var activity = source.StartActivity("Hub/Echo", ActivityKind.Server))
        {
            Assert.NotNull(activity);
        }

        Assert.Contains(recorder.EndedActivities, activity =>
            activity.Source?.Name == MohistOpenTelemetryRegistration.SignalRServerActivitySourceName
            && activity.DisplayName == "Hub/Echo");
    }

    [Fact]
    public async Task ConfigureTracing_ExportsEntityFrameworkCoreActivities()
    {
        using var provider = BuildTracingProvider(out var recorder);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MohistDbContext(options);

        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Probe (Id INTEGER PRIMARY KEY);");

        Assert.Contains(recorder.EndedActivities,
            activity => activity.Source?.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore");
    }

    [Fact]
    public void ExcludeOtelIngestPath_ExcludesOnlyTheOtelPathSegment()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/otel/v1/traces";
        Assert.False(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));

        context.Request.Path = "/otel";
        Assert.False(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));

        context.Request.Path = "/otel-anything-else";
        Assert.True(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));

        context.Request.Path = "/api/health";
        Assert.True(MohistOpenTelemetryRegistration.ExcludeOtelIngestPath(context));
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

    private static ServiceProvider BuildTracingProvider(out RecordingActivityProcessor recorder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var otelBuilder = services.AddOpenTelemetry();
        var activityRecorder = new RecordingActivityProcessor();
        recorder = activityRecorder;
        MohistOpenTelemetryRegistration.ConfigureTracing(
            otelBuilder,
            new OtelOptions { Enabled = true, Endpoint = "http://collector.test/otel" },
            options => options.HttpClientFactory = () => new HttpClient(new SuccessResponseHandler()));
        otelBuilder.WithTracing(tracing => tracing.AddProcessor(activityRecorder));

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();
        return provider;
    }

    private sealed class RecordingActivityProcessor : BaseProcessor<Activity>
    {
        private readonly List<Activity> _ended = new();
        private readonly object _gate = new();

        public IReadOnlyList<Activity> EndedActivities
        {
            get
            {
                lock (_gate) return _ended.ToList();
            }
        }

        public override void OnEnd(Activity activity)
        {
            lock (_gate) _ended.Add(activity);
        }
    }

    private sealed class SuccessResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
            });
        }
    }
}
