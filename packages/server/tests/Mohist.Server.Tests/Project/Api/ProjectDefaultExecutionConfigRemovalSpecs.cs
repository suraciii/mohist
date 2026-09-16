using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Project.Api;

/// <summary>
/// The Project-level default execution configuration is deleted, not
/// renamed: its write routes no longer resolve and the Project read carries
/// no field for it.
/// </summary>
[Trait("level", "L1")]
public class ProjectDefaultExecutionConfigRemovalSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly HttpClient _client;

    public ProjectDefaultExecutionConfigRemovalSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task DefaultExecutionConfigRoutes_ReturnNotFound()
    {
        var projectId = await CreateProjectAsync();

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(
                method,
                $"/api/projects/{projectId}/default-execution-config")
            {
                Content = JsonContent.Create(new { runtime = "pi", model = "provider/model" }),
            };
            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task ProjectRead_DoesNotExposeDefaultExecutionConfig()
    {
        var projectId = await CreateProjectAsync();

        var project = await _client.GetDataAsync<JsonElement>($"/api/projects/{projectId}");

        Assert.False(project.TryGetProperty("defaultExecutionConfig", out _));
    }

    private async Task<string> CreateProjectAsync()
    {
        var created = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new
            {
                name = $"no-exec-default-{Guid.NewGuid():N}",
                verificationCommand = "true",
                repository = new
                {
                    name = "main",
                    gitUrl = $"file://{Guid.NewGuid():N}",
                    baseBranch = "main",
                },
            });
        return created.Id;
    }

    private sealed record ProjectDto(string Id);
}
