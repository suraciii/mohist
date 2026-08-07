using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Config;

public sealed class ConfigHotReloadSpecs
{
    [Fact]
    public void CleanupPolicyOptionsSnapshot_AfterConfigReload_NextScopeReturnsUpdatedBudget()
    {
        const long initialBudget = 10L * 1024 * 1024 * 1024;
        const long updatedBudget = 64L * 1024 * 1024 * 1024;
        using var fixture = new ReloadableOptionsFixture(new Dictionary<string, string?>
        {
            [$"{CleanupPolicyOptions.SectionName}:StorageBudgetBytes"] = initialBudget.ToString(),
        });

        Assert.Equal(initialBudget, fixture.ReadSnapshot().StorageBudgetBytes);

        fixture.Set("StorageBudgetBytes", updatedBudget.ToString());
        fixture.Reload();

        Assert.Equal(updatedBudget, fixture.ReadSnapshot().StorageBudgetBytes);
    }

    [Fact]
    public void CleanupPolicyOptionsSnapshot_AcrossReload_IsNotFrozenAtStartupValue()
    {
        using var fixture = new ReloadableOptionsFixture(new Dictionary<string, string?>
        {
            [$"{CleanupPolicyOptions.SectionName}:RetentionDays"] = "7",
        });

        Assert.Equal(7, fixture.ReadSnapshot().RetentionDays);

        fixture.Set("RetentionDays", "30");
        fixture.Reload();

        Assert.Equal(30, fixture.ReadSnapshot().RetentionDays);
    }

    [Fact]
    public void CleanupPolicyOptionsSnapshot_UnconfiguredSource_YieldsAllNullPolicyFields()
    {
        using var fixture = new ReloadableOptionsFixture(
            new Dictionary<string, string?>());

        var options = fixture.ReadSnapshot();

        Assert.Null(options.RetentionDays);
        Assert.Null(options.StorageBudgetBytes);
        Assert.Null(options.StorageTargetWatermarkBytes);
    }

    private sealed class ReloadableOptionsFixture : IDisposable
    {
        private readonly IConfigurationRoot _configuration;
        private readonly ServiceProvider _services;

        public ReloadableOptionsFixture(IReadOnlyDictionary<string, string?> values)
        {
            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
            var services = new ServiceCollection();
            services.AddOptions();
            services.Configure<CleanupPolicyOptions>(
                _configuration.GetSection(CleanupPolicyOptions.SectionName));
            _services = services.BuildServiceProvider();
        }

        public void Set(string name, string value) =>
            _configuration[$"{CleanupPolicyOptions.SectionName}:{name}"] = value;

        public void Reload() => _configuration.Reload();

        public CleanupPolicyOptions ReadSnapshot()
        {
            using var scope = _services.CreateScope();
            return scope.ServiceProvider
                .GetRequiredService<IOptionsSnapshot<CleanupPolicyOptions>>()
                .Value;
        }

        public void Dispose()
        {
            _services.Dispose();
            if (_configuration is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
