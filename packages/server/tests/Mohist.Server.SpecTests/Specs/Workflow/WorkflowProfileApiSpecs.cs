using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
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
