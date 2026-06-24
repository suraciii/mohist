using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Config;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Wires the server's trace-only OpenTelemetry pipeline into host DI.
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

        ConfigureTracing(services.AddOpenTelemetry(), options);
        return services;
    }

    internal static void ConfigureTracing(OpenTelemetryBuilder builder, OtelOptions options)
        => ConfigureTracing(builder, options, configureExporter: null);

    internal static void ConfigureTracing(
        OpenTelemetryBuilder builder,
        OtelOptions options,
        Action<OtlpExporterOptions>? configureExporter)
    {
        var exportEndpoint = ResolveExportEndpoint(options.Endpoint);

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
                    !IsExporterSelfFeedback(msg.RequestUri, exportEndpoint))
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter(otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = exportEndpoint;
                    configureExporter?.Invoke(otlp);
                });
        });

        // Mirror the trace-OTLP configuration into the Options pattern
        // so production observability tooling (and tests) can resolve
        // IOptions<OtlpExporterOptions> without invoking the SDK's
        // internal TracerProvider builder. The SDK does not register
        // an IConfigureOptions<OtlpExporterOptions> on its own for the
        // trace-only path, so without this mirroring the configured
        // values would be opaque to anyone reading via DI.
        builder.Services.Configure<OtlpExporterOptions>(otlp =>
        {
            otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
            otlp.Endpoint = exportEndpoint;
        });
    }

    /// <summary>
    /// Resolve the user-configured OTLP HTTP base endpoint to the full
    /// trace-export URL. Appends <c>/v1/traces</c> when the configured
    /// base URL does not already end with it.
    /// </summary>
    internal static Uri ResolveExportEndpoint(string baseEndpoint)
    {
        var uri = new Uri(baseEndpoint);
        if (uri.AbsolutePath.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var trimmed = uri.AbsolutePath.TrimEnd('/');
        return new Uri(uri, $"{trimmed}/v1/traces");
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
