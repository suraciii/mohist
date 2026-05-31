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
    public async Task GivenUserChoosesDefaultAndStageModels_WhenSettingsAreSaved_ThenMohistUsesThoseRuntimePreferences()
    {
        await _client.PostOkAsync("/api/projects", new { name = $"settings-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        await _client.PutAsJsonOkAsync("/api/opencode-model", new { model = "openai/gpt-4o" });
        await _client.PutAsJsonOkAsync("/api/stage-models", new { stageModels = new Dictionary<string, string> { ["plan"] = "anthropic/claude" } });

        var model = await _client.GetDataAsync<ModelDto>("/api/opencode-model");
        var stageModels = await _client.GetDataAsync<StageModelsDto>("/api/stage-models");

        Assert.Equal("openai/gpt-4o", model.Model);
        Assert.Equal("anthropic/claude", stageModels.StageModels!["plan"]);
    }

    private sealed record ModelDto(string? Model);
    private sealed record StageModelsDto(Dictionary<string, string>? StageModels);
}
