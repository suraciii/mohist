using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

[Collection("IntegrationMisc")]
public class RuntimeSettingsSpecs
{
    private readonly HttpClient _client;

    public RuntimeSettingsSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GivenUserChoosesDefaultAndStageModels_WhenSettingsPatchProjectVariables_ThenMohistUsesThoseRuntimePreferences()
    {
        var projectName = $"settings-{Guid.NewGuid():N}";
        await _client.PostOkAsync("/api/projects", new
        {
            name = projectName,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        var projectId = (await _client.GetDataAsync<List<ProjectResponse>>("/api/projects")).Single(p => p.Name == projectName).Id;

        await _client.PatchDataAsync<ProjectVariablesDto>($"/api/projects/{projectName}/workflow-profile/variables", new
        {
            vars = new { agent = new { type = "opencode", model = "openai/gpt-4o" } }
        });
        await _client.PatchDataAsync<ProjectVariablesDto>($"/api/projects/{projectName}/workflow-profile/variables", new
        {
            stages = new
            {
                plan = new { vars = new { agent = new { type = "opencode", model = "anthropic/claude" } } },
                build = new { vars = new { agent = new { type = "opencode", model = "openai/gpt-4o" } } }
            }
        });

        var variables = await _client.GetDataAsync<ProjectVariablesDto>($"/api/projects/{projectName}/workflow-profile/variables");

        Assert.NotNull(variables.Vars);
        Assert.Equal("openai/gpt-4o", variables.Vars.RootElement.GetProperty("agent").GetProperty("model").GetString());
        Assert.NotNull(variables.Stages);
        Assert.Equal("anthropic/claude", variables.Stages["plan"].Vars.RootElement.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("openai/gpt-4o", variables.Stages["build"].Vars.RootElement.GetProperty("agent").GetProperty("model").GetString());
    }

    private sealed record ProjectVariablesDto(JsonDocument? Vars, Dictionary<string, ProjectStageVariablesDto>? Stages);
    private sealed record ProjectStageVariablesDto(JsonDocument Vars);
    private sealed record ProjectResponse(string Id, string Name);
}
