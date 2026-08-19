using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

public class IssueRepositoryApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public IssueRepositoryApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _fixture = fixture;
    }

    [Fact]
    public async Task PostIssue_WithUnknownRepositoryName_ReturnsBadRequestWithoutCreatingIssue()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Ghost repo", repositoryName = "ghost" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("ghost", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetIssue_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-repo-unknown-{Guid.NewGuid():N}/issues/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostIssue_UnknownProject_Returns404()
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/proj-repo-unknown-{Guid.NewGuid():N}/issues",
            new { title = "Ghost" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string ProjectId, ProjectInfo Project)> SetupProjectWithRepositoriesAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        var project = await grain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "main", GitUrl = "git@main.example:repo.git", BaseBranch = "main", IsDefault = true });
        await grain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", "develop");
        return (projectId, project);
    }
}
