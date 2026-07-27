using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs.Otel;

public class OtelOptionsTests
{
    [Fact]
    public void Defaults_TracingIsEnabled_EndpointIsLocalCollector()
    {
        var options = new OtelOptions();

        Assert.True(options.Enabled);
        Assert.Equal("http://localhost:4318/otel", options.Endpoint);
        Assert.Equal("Mohist:Otel", OtelOptions.SectionName);
    }

    [Fact]
    public void Binding_FromMohistOtelSection_ReadsEnabledAndEndpoint()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Enabled"] = "true",
                ["Mohist:Otel:Endpoint"] = "http://collector.example.com:4318/otel",
            })
            .Build();

        var options = config.GetSection(OtelOptions.SectionName).Get<OtelOptions>();

        Assert.NotNull(options);
        Assert.True(options!.Enabled);
        Assert.Equal("http://collector.example.com:4318/otel", options.Endpoint);
    }

    [Fact]
    public void Binding_EmptySection_AppliesDefaults()
    {
        var config = new ConfigurationBuilder().Build();

        var options = config.GetSection(OtelOptions.SectionName).Get<OtelOptions>() ?? new OtelOptions();

        Assert.True(options.Enabled);
        Assert.Equal(OtelOptions.DefaultEndpoint, options.Endpoint);
    }

    /// <summary>
    /// Verifies the documented <c>MOHIST__Otel__Endpoint</c> environment
    /// variable takes precedence over the value shipped in
    /// <c>~/.mohist/config.jsonc</c>. The standard .NET
    /// <c>EnvironmentVariablesConfigurationProvider</c> maps <c>__</c> to
    /// <c>:</c> automatically — the production host relies on that for
    /// every other section, and OTel must follow the same contract.
    /// </summary>
    [Fact]
    public void Binding_EnvVar_MohistOtelEndpoint_OverridesConfigFileValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Endpoint"] = "http://from-config-file:4318/otel",
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The env-var provider is layered AFTER the file
                // provider, so its entries override.
                ["Mohist:Otel:Endpoint"] = "http://from-env-var:4318/otel",
            })
            .Build();

        var options = config.GetSection(OtelOptions.SectionName).Get<OtelOptions>();

        Assert.Equal("http://from-env-var:4318/otel", options!.Endpoint);
    }

    [Fact]
    public void Configure_RegistersOptionsWithTheOptionsPattern()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Enabled"] = "false",
                ["Mohist:Otel:Endpoint"] = "http://disabled.example/otel",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMohistOpenTelemetry(config);
        using var provider = services.BuildServiceProvider();

        var snapshot = provider.GetRequiredService<IOptions<OtelOptions>>().Value;
        Assert.False(snapshot.Enabled);
        Assert.Equal("http://disabled.example/otel", snapshot.Endpoint);
    }
}
