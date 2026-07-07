using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Config;

/// <summary>
/// Focused spec for issue-355 T-002. T-001 covers the production
/// AddMohistConfigFile source wiring; this spec locks the downstream options
/// behavior T-002 depends on: after a deterministic IConfiguration reload,
/// IOptionsSnapshot re-binds in a new request scope instead of returning a
/// startup-time value.
/// </summary>
public sealed class ConfigHotReloadSpecs : IDisposable
{
    private readonly string _configPath = Path.Combine(
        Path.GetTempPath(),
        $"mohist-hot-reload-{Guid.NewGuid():N}.jsonc");

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task CleanupPolicyOptionsSnapshot_AfterConfigSourceReload_NextScopeReturnsUpdatedBudget()
    {
        const long initialBudget = 10L * 1024 * 1024 * 1024;
        const long updatedBudget = 64L * 1024 * 1024 * 1024;

        await WriteConfigAsync($$"""
            {
              // JSONC comments and trailing commas must stay valid through the
              // JSON provider used by production AddMohistConfigFile.
              "Mohist": {
                "WorkspaceCleanup": {
                  "StorageBudgetBytes": {{initialBudget}},
                },
              },
            }
            """);
        using var harness = BuildServices();

        Assert.Equal(initialBudget, harness.ReadSnapshot().StorageBudgetBytes);

        await WriteConfigAsync($$"""
            {
              "Mohist": {
                "WorkspaceCleanup": {
                  "StorageBudgetBytes": {{updatedBudget}},
                },
              },
            }
            """);
        harness.Reload();

        Assert.Equal(updatedBudget, harness.ReadSnapshot().StorageBudgetBytes);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task CleanupPolicyOptionsSnapshot_AcrossReload_IsNotFrozenAtStartupValue()
    {
        await WriteConfigAsync("""
            { "Mohist": { "WorkspaceCleanup": { "RetentionDays": 7 } } }
            """);
        using var harness = BuildServices();

        Assert.Equal(7, harness.ReadSnapshot().RetentionDays);

        await WriteConfigAsync("""
            { "Mohist": { "WorkspaceCleanup": { "RetentionDays": 30 } } }
            """);
        harness.Reload();

        Assert.Equal(30, harness.ReadSnapshot().RetentionDays);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task CleanupPolicyOptionsSnapshot_UnconfiguredSource_YieldsAllNullPolicyFields()
    {
        await WriteConfigAsync("{}");
        using var harness = BuildServices();

        var options = harness.ReadSnapshot();

        Assert.Null(options.RetentionDays);
        Assert.Null(options.StorageBudgetBytes);
        Assert.Null(options.StorageTargetWatermarkBytes);
    }

    private OptionsHarness BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            // The production AddMohistConfigFile wiring uses reloadOnChange:
            // true and T-001 tests that source shape directly. This spec
            // forces reload deterministically through IConfigurationRoot, so a
            // watcher is unnecessary and would make the test environment do
            // extra OS-level work unrelated to T-002's options behavior.
            .AddJsonFile(_configPath, optional: false, reloadOnChange: false)
            .Build();
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<CleanupPolicyOptions>(
            configuration.GetSection(CleanupPolicyOptions.SectionName));
        return new OptionsHarness(configuration, services.BuildServiceProvider());
    }

    private sealed class OptionsHarness : IDisposable
    {
        private readonly IConfigurationRoot _configuration;
        private readonly ServiceProvider _services;

        public OptionsHarness(IConfigurationRoot configuration, ServiceProvider services)
        {
            _configuration = configuration;
            _services = services;
        }

        public void Reload() => _configuration.Reload();

        public CleanupPolicyOptions ReadSnapshot()
        {
            using var scope = _services.CreateScope();
            var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<CleanupPolicyOptions>>();
            return snapshot.Value;
        }

        public void Dispose()
        {
            _services.Dispose();
            if (_configuration is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private Task WriteConfigAsync(string content) =>
        File.WriteAllTextAsync(_configPath, content);

    public void Dispose()
    {
        if (File.Exists(_configPath))
            File.Delete(_configPath);
    }
}
