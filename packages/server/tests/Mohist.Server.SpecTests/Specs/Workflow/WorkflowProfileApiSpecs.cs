using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

[Collection("IntegrationRunner")]
public class WorkflowProfileApiSpecs
{
    private readonly HttpClient _client;

    public WorkflowProfileApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task PostMalformedYaml_ReturnsDefinitionValidationAndDoesNotPersist()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", new
        {
            profileId = "broken",
            name = "Broken",
            definitionSource = "stages: [",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_profile_definition_validation", json.GetProperty("code").GetString());
        Assert.Contains(
            json.GetProperty("details").EnumerateArray(),
            error => string.Equals(error.GetProperty("source").GetString(), "definition", StringComparison.OrdinalIgnoreCase));

        var profiles = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");
        Assert.DoesNotContain(profiles.EnumerateArray(), profile => profile.GetProperty("profileId").GetString() == "broken");
    }

    [Fact]
    public async Task ListAndGet_ExposeAgentRuntime()
    {
        var project = await CreateProjectAsync();

        var profiles = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles");
        var builtin = profiles.EnumerateArray()
            .Single(profile => profile.GetProperty("profileId").GetString() == "mohist/local");
        var detail = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/workflow-profiles/mohist%2Flocal");

        Assert.Equal("opencode", builtin.GetProperty("agentRuntime").GetString());
        Assert.Equal("opencode", detail.GetProperty("agentRuntime").GetString());
        Assert.Equal("mohist/local", detail.GetProperty("profileId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("definitionSource").GetString()));
        var stage = detail.GetProperty("stages").EnumerateArray()
            .Single(candidate => candidate.GetProperty("stage").GetString() == "plan");
        Assert.Equal("plan", stage.GetProperty("stage").GetString());
        Assert.NotEmpty(stage.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task PutMalformedYaml_ReturnsDefinitionValidationAndPreservesStoredProfile()
    {
        var project = await CreateProjectAsync();
        var valid = new
        {
            profileId = "editable",
            name = "Editable",
            definitionSource = "stages:\n  - stage: build\n    tasks: []\n    checks: []\n",
        };
        using var create = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles", valid);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var response = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/workflow-profiles/editable", new
        {
            profileId = "editable",
            name = "Editable",
            definitionSource = "stages: [",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_profile_definition_validation", json.GetProperty("code").GetString());
        var stored = await _client.GetDataAsync<JsonElement>($"/api/projects/{project.Id}/workflow-profiles/editable");
        Assert.Contains("stage: build", stored.GetProperty("definitionSource").GetString());
    }

    private Task<ProjectInfo> CreateProjectAsync() =>
        _client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects",
            $"workflow-profile-api-{Guid.NewGuid():N}");
}
