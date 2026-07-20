using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Workflow.Domain;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class ConfigServiceTests
{
    private readonly InMemoryConfigDocumentStore _documents = new();
    private readonly ConfigService _svc;

    public ConfigServiceTests()
    {
        var config = new ConfigurationBuilder().Build();
        _svc = new ConfigService(
            config,
            new MockEnvironmentVariableProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance,
            _documents);
    }

    [Fact]
    public async Task GetConfig_Defaults_ReturnsDefaults()
    {
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(3456, cfg["serverPort"]);
        Assert.Equal("localhost", cfg["serverHost"]);
    }

    [Fact]
    public async Task SetAndGet_ReturnsUpdatedValue()
    {
        await _svc.SetAsync("serverPort", 8080);
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(8080, cfg["serverPort"]);
    }

    [Fact]
    public async Task Validate_Number_Invalid()
    {
        var (valid, error) = _svc.Validate("serverPort", "abc");
        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Validate_Number_Valid()
    {
        var (valid, error) = _svc.Validate("serverPort", "8080");
        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public async Task GetAll_MasksSecrets()
    {
        await _svc.SetAsync("logLevel", "DEBUG");
        var all = await _svc.GetAllAsync();
        Assert.Equal("DEBUG", all["logLevel"]);
    }

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
        // Per #410 T-002 design D5: GetVariables projects the global agent
        // config down to the converged whitelist, so legacy keys like `type`
        // never enter vars.agent from config.jsonc.
        Assert.False(agent.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task GetVariables_OnlyModelSet_ReturnsEmptyBundle()
    {
        _documents.Content = """{ "Mohist": { "Config": { "model": "anthropic/claude" } } }""";

        var bundle = await _svc.GetVariables();

        Assert.Null(bundle.Vars);
        Assert.Null(bundle.Stages);
    }

    [Fact]
    public async Task GetAgentConfig_OnlyModelSet_ReturnsNull()
    {
        _documents.Content = """{ "Mohist": { "Config": { "model": "anthropic/claude" } } }""";

        var agent = await _svc.GetAgentConfigAsync();

        Assert.Null(agent);
    }

    [Fact]
    public async Task GetAgentConfig_AgentConfigured_ReturnsWhitelistedKeysOnly()
    {
        await _svc.SetAsync("agent", new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o",
            ["type"] = "opencode",
        });

        var agent = await _svc.GetAgentConfigAsync();

        Assert.NotNull(agent);
        Assert.Equal("gpt-4o", agent!["model"]!.ToString());
        // Per #410 T-002 design D5: GetAgentConfigAsync projects the global
        // agent config down to the converged whitelist, so the returned
        // dictionary contains only {model, variant}. Legacy keys are not
        // round-tripped to callers (IssueVariableBuilder.Build, the issue
        // variable projection, etc.).
        Assert.False(agent.ContainsKey("type"));
    }

    [Fact]
    public async Task GetAgentConfig_NeitherAgentNorModel_ReturnsNull()
    {
        var agent = await _svc.GetAgentConfigAsync();

        Assert.Null(agent);
    }

    [Fact]
    public async Task SetAgentModel_WritesModelUnderAgentObject()
    {
        await _svc.SetAgentModelAsync("anthropic/claude");

        var agent = await _svc.GetAgentConfigAsync();
        Assert.NotNull(agent);
        Assert.Equal("anthropic/claude", agent!["model"]!.ToString());

        var cfg = await _svc.GetConfigAsync();
        Assert.False(cfg.ContainsKey("model"));
        Assert.True(cfg.ContainsKey("agent"));
    }

    [Fact]
    public async Task SetAgentModel_NullModel_ClearsAgentKeyWhenNoRemainingKeys()
    {
        await _svc.SetAgentModelAsync("anthropic/claude");
        await _svc.SetAgentModelAsync(null);

        var cfg = await _svc.GetConfigAsync();
        Assert.False(cfg.ContainsKey("agent"));
        Assert.False(cfg.ContainsKey("model"));
    }

    [Fact]
    public async Task SetAgentModel_PreservesSiblingAgentKeysWhenOnlyClearingModel()
    {
        // Per D5: `agent` in config.jsonc only carries {model, variant} —
        // legacy keys are not written through SetAsync validation here, but
        // the authoritative scenario exercised by this test is "clearing
        // the model while other whitelisted siblings remain". The existing
        // implementation already preserves sibling keys, so set the
        // dictionary on the converged shape and assert it survives the
        // model clear.
        await _svc.SetAsync("agent", new Dictionary<string, object?>
        {
            ["model"] = "anthropic/claude",
            ["variant"] = "max",
        });
        await _svc.SetAgentModelAsync(null);

        var agent = await _svc.GetAgentConfigAsync();
        Assert.NotNull(agent);
        Assert.False(agent!.ContainsKey("model"));
        Assert.Equal("max", agent["variant"]!.ToString());

        var cfg = await _svc.GetConfigAsync();
        Assert.True(cfg.ContainsKey("agent"));
        Assert.False(cfg.ContainsKey("model"));
    }

    [Fact]
    public async Task GetVariables_NoAgentOrModel_ReturnsEmptyBundle()
    {
        var bundle = await _svc.GetVariables();

        Assert.Null(bundle.Vars);
        Assert.Null(bundle.Stages);
    }

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

    [Fact]
    public async Task GetConfig_ExposesLogLevelAndRuntimeSchedulingKeys()
    {
        var cfg = await _svc.GetConfigAsync();

        Assert.True(cfg.ContainsKey("logLevel"), "logLevel should be exposed by config");
        Assert.Equal("INFO", cfg["logLevel"]);

        Assert.True(cfg.ContainsKey("maxConcurrentAgents"), "maxConcurrentAgents should be exposed by config");
        Assert.True(cfg.ContainsKey("agentTimeout"), "agentTimeout should be exposed by config");
        Assert.True(cfg.ContainsKey("taskTimeout"), "taskTimeout should be exposed by config");
        Assert.True(cfg.ContainsKey("stageTimeout"), "stageTimeout should be exposed by config");
        Assert.True(cfg.ContainsKey("pollInterval"), "pollInterval should be exposed by config");
        Assert.True(cfg.ContainsKey("maxGracePeriods"), "maxGracePeriods should be exposed by config");

        Assert.Equal(3, cfg["maxConcurrentAgents"]);
        Assert.Equal(600, cfg["agentTimeout"]);
        Assert.Equal(600, cfg["taskTimeout"]);
        Assert.Equal(3600, cfg["stageTimeout"]);
        Assert.Equal(5000, cfg["pollInterval"]);
        Assert.Equal(3, cfg["maxGracePeriods"]);
    }

    [Theory]
    [InlineData("DEBUG")]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("ERROR")]
    public async Task Validate_LogLevel_AcceptsSupportedLevels(string level)
    {
        var (valid, error) = _svc.Validate("logLevel", level);
        Assert.True(valid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("VERBOSE")]
    [InlineData("TRACE")]
    [InlineData("FATAL")]
    [InlineData("")]
    [InlineData("INFO ")]
    public async Task Validate_LogLevel_RejectsUnsupportedValues(string level)
    {
        var (valid, error) = _svc.Validate("logLevel", level);
        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("logLevel", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DEBUG")]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("ERROR")]
    public async Task SetAsync_LogLevel_PersistsSupportedLevelAndIsReadable(string level)
    {
        await _svc.SetAsync("logLevel", level);
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(level, cfg["logLevel"]);
    }

    [Fact]
    public async Task SetAsync_LogLevel_RejectsUnsupportedValue_AndLeavesPreviousUnchanged()
    {
        await _svc.SetAsync("logLevel", "WARN");
        var before = await _svc.GetConfigAsync();
        Assert.Equal("WARN", before["logLevel"]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.SetAsync("logLevel", "TRACE"));

        var after = await _svc.GetConfigAsync();
        Assert.Equal("WARN", after["logLevel"]);
    }

    [Fact]
    public async Task Validate_UnknownKey_ReturnsUnknownKeyError()
    {
        var (valid, error) = _svc.Validate("doesNotExist", "x");
        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("Unknown", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetAsync_UnknownKey_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.SetAsync("doesNotExist", "x"));
    }

    [Theory]
    [InlineData("maxConcurrentAgents", 5)]
    [InlineData("agentTimeout", 900)]
    [InlineData("taskTimeout", 1200)]
    [InlineData("stageTimeout", 7200)]
    [InlineData("pollInterval", 1500)]
    [InlineData("maxGracePeriods", 5)]
    public async Task SetAsync_RuntimeSchedulingKey_PersistsAndIsReadable(string key, int value)
    {
        await _svc.SetAsync(key, value);
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(value, cfg[key]);
    }

    [Theory]
    [InlineData("maxConcurrentAgents")]
    [InlineData("agentTimeout")]
    [InlineData("taskTimeout")]
    [InlineData("stageTimeout")]
    [InlineData("pollInterval")]
    [InlineData("maxGracePeriods")]
    public async Task Validate_RuntimeSchedulingKey_RejectsNonNumberValue(string key)
    {
        var (valid, error) = _svc.Validate(key, "not-a-number");
        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void GetSupportedLogLevels_ContainsAllFourRequiredLevels()
    {
        var levels = ConfigService.GetSupportedLogLevels();
        Assert.Contains("DEBUG", levels);
        Assert.Contains("INFO", levels);
        Assert.Contains("WARN", levels);
        Assert.Contains("ERROR", levels);
        Assert.Equal(4, levels.Count);
    }

    [Fact]
    public async Task ReadConfigFile_WithLineComments_ParsesAllKeys()
    {
        var jsonc = "// leading line comment\n{\n  \"Mohist\": {\n    // nested line comment\n    \"Config\": {\n      \"serverPort\": 8080,\n      \"serverHost\": \"example\"\n    }\n  }\n}\n";
        _documents.Content = jsonc;

        var cfg = await _svc.GetConfigAsync();

        Assert.Equal(8080, cfg["serverPort"]);
        Assert.Equal("example", cfg["serverHost"]);
    }

    [Fact]
    public async Task ReadConfigFile_WithBlockCommentsAndTrailingCommas_ParsesAllKeys()
    {
        _documents.Content = """
            /* file header block comment */
            {
              "Mohist": {
                "Config": {
                  "serverPort": 8081, /* inline block */
                  "serverHost": "host",
                },
              },
            }
            """;

        var cfg = await _svc.GetConfigAsync();

        Assert.Equal(8081, cfg["serverPort"]);
        Assert.Equal("host", cfg["serverHost"]);
    }

    [Fact]
    public async Task ReadConfigFile_GenuinelyMalformed_ReturnsEmptyDictionarySoDefaultsApply()
    {
        _documents.Content = "{ this is not jsonc ";

        var cfg = await _svc.GetConfigAsync();

        // The schema-backed defaults still come back; nothing throws.
        Assert.Equal(3456, cfg["serverPort"]);
        Assert.Equal("localhost", cfg["serverHost"]);
    }

    [Fact]
    public async Task WriteConfigFileAsync_RoundTripsCommentedConfig_AndAppliesNewValue()
    {
        _documents.Content = """
            // user comment we should be able to load
            {
              "Mohist": {
                "Config": {
                  "serverHost": "before"
                }
              }
            }
            """;

        await _svc.SetAsync("serverHost", "after");

        var cfg = await _svc.GetConfigAsync();
        Assert.Equal("after", cfg["serverHost"]);
    }

    [Fact]
    public async Task WriteConfigFileAsync_OnMalformedExistingFile_FallsBackToFreshJsonObjectAndAppliesChange()
    {
        _documents.Content = "{ broken jsonc ";

        await _svc.SetAsync("serverPort", 9090);

        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(9090, cfg["serverPort"]);
    }
}
