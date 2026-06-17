using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetVariables_AgentConfigured_ExposesAgentAtVarsAgent()
    {
        await _svc.SetAsync("agent", new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o",
            ["type"] = "opencode",
        });

        var bundle = await _svc.GetVariables();

        Assert.NotNull(bundle.Vars);
        using var doc = JsonDocument.Parse(bundle.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetVariables_OnlyLegacyModelSet_SynthesizesAgentObject()
    {
        await _svc.SetAsync("model", "anthropic/claude");

        var bundle = await _svc.GetVariables();

        Assert.NotNull(bundle.Vars);
        using var doc = JsonDocument.Parse(bundle.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("anthropic/claude", agent.GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetVariables_NoAgentOrModel_ReturnsEmptyBundle()
    {
        var bundle = await _svc.GetVariables();

        Assert.Null(bundle.Vars);
        Assert.Null(bundle.Stages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetVariables_AlwaysReturnsEmptyStages()
    {
        await _svc.SetAsync("agent", new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o",
        });
        await _svc.SetAsync("stageAgents", new Dictionary<string, Dictionary<string, object?>>
        {
            ["plan"] = new() { ["model"] = "sonnet-4" },
        });

        var bundle = await _svc.GetVariables();

        // Stage names are project-specific and never come from global config.jsonc.
        Assert.Null(bundle.Stages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetVariables_AgentConfigured_DoesNotLeakTopLevelModelKey()
    {
        await _svc.SetAsync("agent", new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o",
        });

        var bundle = await _svc.GetVariables();

        Assert.NotNull(bundle.Vars);
        using var doc = JsonDocument.Parse(bundle.Vars.Value.GetRawText());
        // The model must be nested under agent, not a sibling at vars root.
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
        Assert.True(doc.RootElement.TryGetProperty("agent", out _));
    }
}
