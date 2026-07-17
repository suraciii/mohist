using Microsoft.Extensions.Configuration;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public sealed class SqliteConnectionStringTests
{
    [Fact]
    public void DefaultConnectionString_AppliesBusyTimeoutSoConcurrentWritersWait()
    {
        var configuration = new ConfigurationBuilder().Build();
        var resolved = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);

        Assert.Equal("5", ReadValue(resolved, "busy_timeout"));
    }

    [Fact]
    public void ConfiguredConnectionString_KeepsExplicitBusyTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = "Data Source=/tmp/x.db;busy_timeout=30",
            })
            .Build();
        var resolved = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);

        Assert.Equal("30", ReadValue(resolved, "busy_timeout"));
    }

    [Fact]
    public void InMemoryConnectionString_StillGetsBusyTimeoutForTestStability()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = "Data Source=:memory:",
            })
            .Build();
        var resolved = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);

        Assert.Equal("5", ReadValue(resolved, "busy_timeout"));
    }

    [Fact]
    public void ExplicitZeroBusyTimeout_RespectedAsExplicitConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = "Data Source=/tmp/x.db;busy_timeout=0",
            })
            .Build();
        var resolved = MohistServiceRegistration.ResolveSqliteConnectionString(configuration);

        // Any parseable busy_timeout is treated as an explicit setting and
        // left untouched; the default is only applied when the key is absent.
        Assert.Equal("0", ReadValue(resolved, "busy_timeout"));
        Assert.Single(
            resolved.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()),
            p => p.StartsWith("busy_timeout", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadValue(string connectionString, string key)
    {
        foreach (var part in connectionString.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var k = part[..eq].Trim();
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return null;
    }
}

