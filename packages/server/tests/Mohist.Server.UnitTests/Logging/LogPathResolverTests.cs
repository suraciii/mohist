using Microsoft.Extensions.Configuration;
using Mohist.Server.Logging;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.Logging;

public class LogPathResolverTests
{
    [Fact]
    public void Resolve_WhenLogsPathConfigured_ReturnsConfiguredPath()
    {
        var configured = Path.Combine(Path.GetTempPath(), $"mohist-logs-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LogPathResolver.ConfigurationKey] = configured,
            })
            .Build();

        var resolver = new LogPathResolver(configuration, new MockEnvironmentVariableProvider());

        Assert.Equal(configured, resolver.Resolve());
    }

    [Fact]
    public void Resolve_WhenLogsPathBlank_FallsBackToHomeDotMohistLogs()
    {
        var home = Path.Combine(Path.GetTempPath(), $"mohist-home-{Guid.NewGuid():N}");
        var environment = new MockEnvironmentVariableProvider();
        environment["HOME"] = home;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LogPathResolver.ConfigurationKey] = "  ",
            })
            .Build();

        var resolver = new LogPathResolver(configuration, environment);

        var expected = Path.Combine(home, ".mohist", "logs");
        Assert.Equal(expected, resolver.Resolve());
    }

    [Fact]
    public void Resolve_WhenLogsPathUnset_FallsBackToHomeDotMohistLogs()
    {
        var home = Path.Combine(Path.GetTempPath(), $"mohist-home-{Guid.NewGuid():N}");
        var environment = new MockEnvironmentVariableProvider();
        environment["HOME"] = home;
        var configuration = new ConfigurationBuilder().Build();

        var resolver = new LogPathResolver(configuration, environment);

        var expected = Path.Combine(home, ".mohist", "logs");
        Assert.Equal(expected, resolver.Resolve());
    }

    [Fact]
    public void Resolve_WhenHomeUnset_FallsBackToEnvironmentUserProfile()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new MockEnvironmentVariableProvider();

        var resolver = new LogPathResolver(configuration, environment);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, ".mohist", "logs");
        Assert.Equal(expected, resolver.Resolve());
    }
}
