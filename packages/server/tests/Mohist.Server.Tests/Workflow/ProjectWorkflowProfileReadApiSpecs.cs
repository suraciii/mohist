using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Workflow;

[Trait("level", "L1")]
public sealed class ProjectWorkflowProfileReadApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly HttpClient _client;

    public ProjectWorkflowProfileReadApiSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetDefault_ReturnsConfiguredProfileAndDisabledIds()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects",
            $"workflow-profile-read-{Guid.NewGuid():N}");

        var response = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/workflow-profile/default");

        Assert.Equal(project.Id, response.GetProperty("projectId").GetString());
        Assert.Equal("mohist/local", response.GetProperty("defaultWorkflowProfileId").GetString());
        Assert.Empty(response.GetProperty("disabledWorkflowProfileIds").EnumerateArray());
    }
}
