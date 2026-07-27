using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public class OtelOptionsTests
{
    [Fact]
    public void Defaults_CollectorIsEnabled()
    {
        var options = new OtelOptions();

        Assert.Equal(4318, options.Port);
        Assert.True(options.Enabled);
        Assert.Null(options.DbPath);
        Assert.Equal("Mohist:Otel", OtelOptions.SectionName);
        Assert.Equal("MOHIST_OTEL_DB_PATH", OtelOptions.DbPathEnvironmentVariable);
        Assert.Equal(TimeSpan.FromHours(72), options.RetentionMaxAge);
        Assert.Equal(RuntimeValueRules.StorageBudgetBytes, options.StorageBudgetBytes);
        Assert.Equal(1_073_741_824L, options.StorageBudgetBytes);
    }

    [Fact]
    public void Configuration_OverrideAppliesToBoundInstance()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:Otel:Port"] = "14318",
                ["Mohist:Otel:Enabled"] = "false",
                ["Mohist:Otel:DbPath"] = "/tmp/custom-otel.db",
                ["Mohist:Otel:StorageBudgetBytes"] = "2147483648",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<OtelOptions>(config.GetSection(OtelOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var bound = provider.GetRequiredService<IOptions<OtelOptions>>().Value;

        Assert.Equal(14318, bound.Port);
        Assert.False(bound.Enabled);
        Assert.Equal("/tmp/custom-otel.db", bound.DbPath);
        Assert.Equal(2_147_483_648L, bound.StorageBudgetBytes);
    }

    [Fact]
    public void Configuration_DefaultsWhenSectionMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<OtelOptions>(config.GetSection(OtelOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var bound = provider.GetRequiredService<IOptions<OtelOptions>>().Value;

        Assert.Equal(4318, bound.Port);
        Assert.True(bound.Enabled);
        Assert.Null(bound.DbPath);
        Assert.Equal(TimeSpan.FromHours(72), bound.RetentionMaxAge);
        Assert.Equal(RuntimeValueRules.StorageBudgetBytes, bound.StorageBudgetBytes);
    }
}
