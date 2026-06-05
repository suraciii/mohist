using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class RuntimeSettingsSpecs
{
    private readonly HttpClient _client;

    public RuntimeSettingsSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GivenUserChoosesDefaultAndStageAgents_WhenSettingsAreSaved_ThenMohistUsesThoseRuntimePreferences()
    {
        await _client.PostOkAsync("/api/projects", new { name = $"settings-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        await _client.PutAsJsonOkAsync("/api/opencode-model", new { model = "openai/gpt-4o" });
        await _client.PutAsJsonOkAsync("/api/agent-config", new
        {
            agent = new Dictionary<string, object?> { ["model"] = "openai/gpt-4o" },
            stageAgents = new Dictionary<string, Dictionary<string, object?>>
            {
                ["plan"] = new() { ["model"] = "anthropic/claude" }
            }
        });

        var model = await _client.GetDataAsync<ModelDto>("/api/opencode-model");
        var agentConfig = await _client.GetDataAsync<AgentConfigDto>("/api/agent-config");

        Assert.Equal("openai/gpt-4o", model.Model);
        Assert.NotNull(agentConfig.StageAgents);
        Assert.Equal("anthropic/claude", agentConfig.StageAgents["plan"]["model"]?.ToString());
    }

    private sealed record ModelDto(string? Model);
    private sealed record AgentConfigDto(Dictionary<string, object?>? Agent, Dictionary<string, Dictionary<string, object?>>? StageAgents);
}
