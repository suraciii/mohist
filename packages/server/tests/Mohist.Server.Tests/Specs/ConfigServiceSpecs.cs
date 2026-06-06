using Microsoft.Extensions.Configuration;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs;

public class ConfigServiceSpecs : IAsyncLifetime
{
    private ConfigService _svc = null!;
    private string _configPath = null!;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().Build();
        _configPath = Path.Combine(Path.GetTempPath(), $"mohist-config-{Guid.NewGuid():N}.jsonc");
        _svc = new ConfigService(config, new MockEnvironmentVariableProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance, _configPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
        return Task.CompletedTask;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConfig_Defaults_ReturnsDefaults()
    {
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(3456, cfg["serverPort"]);
        Assert.Equal("localhost", cfg["serverHost"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task SetAndGet_ReturnsUpdatedValue()
    {
        await _svc.SetAsync("serverPort", 8080);
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(8080, cfg["serverPort"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Validate_Number_Invalid()
    {
        var (valid, error) = _svc.Validate("serverPort", "abc");
        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Validate_Number_Valid()
    {
        var (valid, error) = _svc.Validate("serverPort", "8080");
        Assert.True(valid);
        Assert.Null(error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetAll_MasksSecrets()
    {
        await _svc.SetAsync("model", "anthropic/claude");
        var all = await _svc.GetAllAsync();
        Assert.Equal("anthropic/claude", all["model"]);
    }
}
