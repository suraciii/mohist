using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using OpenTelemetry.Exporter;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs.Otel;

/// <summary>
/// Tests that verify the OTLP exporter is configured with the
/// documented protocol (HTTP/Protobuf) and endpoint.
/// </summary>
[Collection("OtelTracing")]
public class OtelExporterConfigurationTests
{
    [Fact]
    public void ConfigureTracing_OtlpExporterEndpoint_AppendsV1TracesToBase()
    {
        // The trace-specific AddOtlpExporter(Action<OtlpExporterOptions>)
        // overload applies the inline configure delegate when the SDK
        // builds the exporter — it does NOT register the delegate via
        // services.Configure. To make the configured values inspectable
        // for tests, AddMohistOpenTelemetry ALSO registers the same
        // configuration via services.Configure<OtlpExporterOptions>.
        // Resolving IOptions<OtlpExporterOptions> then yields the
        // configured protocol + endpoint so the test can assert what
        // the export pipeline will receive.
        //
        // /v1/traces is appended to the base URL because the SDK only
        // auto-appends when the endpoint comes from spec env vars; the
        // explicit-set path requires us to do it.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Enabled"] = "true",
                ["Mohist:Otel:Endpoint"] = "http://collector.example/otel",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);

        using var provider = services.BuildServiceProvider();
        var optionsAccessor = provider.GetRequiredService<IOptions<OtlpExporterOptions>>();

        Assert.Equal(OtlpExportProtocol.HttpProtobuf, optionsAccessor.Value.Protocol);
        Assert.Equal(new Uri("http://collector.example/otel/v1/traces"), optionsAccessor.Value.Endpoint);
    }

    [Fact]
    public void ConfigureTracing_OtlpExporterEndpoint_ReflectsConfiguredOtelOptions()
    {
        // Same as above, but parameterized on the configured endpoint
        // so we can flip through several hostnames and ports and
        // confirm every one is forwarded verbatim with /v1/traces
        // appended. This is the production-config-to-options bridge:
        // changing Mohist:Otel:Endpoint in config (or via
        // MOHIST__Otel__Endpoint) must propagate to the exporter.
        foreach (var (baseEndpoint, expected) in new[]
                 {
                     ("http://localhost:4318/otel", "http://localhost:4318/otel/v1/traces"),
                     ("http://localhost:4318", "http://localhost:4318/v1/traces"),
                     ("https://otel.example.com:443/otel", "https://otel.example.com:443/otel/v1/traces"),
                     ("http://10.0.0.1:4318/some/nested/path", "http://10.0.0.1:4318/some/nested/path/v1/traces"),
                 })
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mohist:Otel:Enabled"] = "true",
                    ["Mohist:Otel:Endpoint"] = baseEndpoint,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddMohistOpenTelemetry(config);

            using var provider = services.BuildServiceProvider();
            var optionsAccessor = provider.GetRequiredService<IOptions<OtlpExporterOptions>>();

            Assert.Equal(OtlpExportProtocol.HttpProtobuf, optionsAccessor.Value.Protocol);
            Assert.Equal(new Uri(expected), optionsAccessor.Value.Endpoint);
        }
    }
}
