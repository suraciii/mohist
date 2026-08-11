using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Events;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Wires the server's OpenTelemetry pipeline into host DI.
/// </summary>
public static class MohistOpenTelemetryRegistration
{
    public const string ServiceName = "Mohist.Server";

    public static readonly PathString OtelIngestPathPrefix = "/otel";

    public const string SignalRServerActivitySourceName = "Microsoft.AspNetCore.SignalR.Server";

    public static readonly string[] OrleansActivitySourceNames = new[]
    {
        "Microsoft.Orleans.Application",
        "Microsoft.Orleans.Runtime",
        "Microsoft.Orleans.Lifecycle",
        "Microsoft.Orleans.Storage",
    };

    public static IServiceCollection AddMohistOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OtelOptions>(configuration.GetSection(OtelOptions.SectionName));

        var options = configuration.GetSection(OtelOptions.SectionName).Get<OtelOptions>() ?? new OtelOptions();
        if (!options.Enabled)
        {
            return services;
        }

        ConfigureTelemetry(services.AddOpenTelemetry(), options);
        return services;
    }

    internal static void ConfigureTelemetry(OpenTelemetryBuilder builder, OtelOptions options)
        => ConfigureTelemetry(builder, options, configureExporter: null);

    internal static void ConfigureTelemetry(
        OpenTelemetryBuilder builder,
        OtelOptions options,
        Action<OtlpExporterOptions>? configureExporter)
    {
        var traceExportEndpoint = ResolveExportEndpoint(options.Endpoint);
        var metricsExportEndpoint = ResolveMetricsExportEndpoint(options.Endpoint);

        builder.WithTracing(tracing =>
        {
            tracing
                .ConfigureResource(resource => resource.AddService(ServiceName))
                .AddAspNetCoreInstrumentation(o => o.Filter = ExcludeOtelIngestPath)
                .AddSource(SignalRServerActivitySourceName)
                .AddSource(OrleansActivitySourceNames[0])
                .AddSource(OrleansActivitySourceNames[1])
                .AddSource(OrleansActivitySourceNames[2])
                .AddSource(OrleansActivitySourceNames[3])
                .AddHttpClientInstrumentation(o => o.FilterHttpRequestMessage = msg =>
                    !IsExporterSelfFeedback(msg.RequestUri, traceExportEndpoint))
                .AddEntityFrameworkCoreInstrumentation();
            if (options.ExportEnabled)
            {
                tracing.AddOtlpExporter("tracing", configure: null);
                tracing.ConfigureServices(services => services.PostConfigure<OtlpExporterOptions>("tracing", otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = traceExportEndpoint;
                    configureExporter?.Invoke(otlp);
                }));
            }
        });

        builder.WithMetrics(metrics =>
        {
            metrics
                .ConfigureResource(resource => resource.AddService(ServiceName))
                .AddMeter(EventDispatcherService.MeterName);
            if (options.ExportEnabled)
            {
                metrics.AddOtlpExporter("metrics", configure: null);
                metrics.ConfigureServices(services => services.PostConfigure<OtlpExporterOptions>("metrics", otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = metricsExportEndpoint;
                    configureExporter?.Invoke(otlp);
                }));
            }
        });
    }

    /// <summary>
    /// Resolve the user-configured OTLP HTTP base endpoint to the full
    /// trace-export URL. Appends <c>/v1/traces</c> when the configured
    /// base URL does not already end with it.
    /// </summary>
    internal static Uri ResolveExportEndpoint(string baseEndpoint)
        => ResolveExportEndpoint(baseEndpoint, "traces");

    internal static Uri ResolveMetricsExportEndpoint(string baseEndpoint)
        => ResolveExportEndpoint(baseEndpoint, "metrics");

    private static Uri ResolveExportEndpoint(string baseEndpoint, string signal)
    {
        var uri = new Uri(baseEndpoint);
        if (uri.AbsolutePath.EndsWith($"/v1/{signal}", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var trimmed = uri.AbsolutePath.TrimEnd('/');
        return new Uri(uri, $"{trimmed}/v1/{signal}");
    }

    internal static bool ExcludeOtelIngestPath(HttpContext httpContext)
    {
        return !httpContext.Request.Path.StartsWithSegments(OtelIngestPathPrefix);
    }

    internal static bool IsExporterSelfFeedback(Uri? requestUri, Uri exportEndpoint)
    {
        if (requestUri is null)
        {
            return false;
        }

        if (!string.Equals(requestUri.Scheme, exportEndpoint.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(requestUri.Host, exportEndpoint.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requestUri.Port != exportEndpoint.Port)
        {
            return false;
        }

        return requestUri.AbsolutePath.StartsWith(exportEndpoint.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }
}
