using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Api;

[Collection("IntegrationRunner")]
public class ProjectApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public ProjectApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task PostProject_NameOnly_CreatesProjectWithoutPathFields()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "api-pathless" });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.GetProperty("success").GetBoolean());
        var data = json.GetProperty("data");
        Assert.Equal("api-pathless", data.GetProperty("name").GetString());
        Assert.False(data.TryGetProperty("path", out _));
        Assert.False(data.TryGetProperty("effectivePath", out _));
        Assert.False(data.TryGetProperty("baseBranch", out _));
    }

    [Fact]
    public async Task PostProject_NameOnly_DoesNotCreateDefaultRepository()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "no-default-repo" });

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        Assert.Empty(repos);
    }

    [Fact]
    public async Task GetProjects_ListReturnsProjectsWithoutPathFields()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "list-test" });

        var list = await _client.GetDataAsync<List<ProjectInfo>>("/api/projects");
        var project = list.Single(p => p.Id == created.Id);
        Assert.Equal("list-test", project.Name);
        Assert.Null(project.GetType().GetProperty("Path"));
        Assert.Null(project.GetType().GetProperty("BaseBranch"));
    }

    [Fact]
    public async Task ProjectUse_AndDelete_RemainFunctional()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "use-delete-test" });

        await _client.PostOkAsync($"/api/projects/{created.Id}/use");

        var fetched = await _client.GetDataAsync<ProjectInfo>($"/api/projects/{created.Id}");
        Assert.Equal("use-delete-test", fetched.Name);

        using var deleteResponse = await _client.DeleteAsync($"/api/projects/{created.Id}");
        deleteResponse.EnsureSuccessStatusCode();

        var list = await _client.GetDataAsync<List<ProjectInfo>>("/api/projects");
        Assert.DoesNotContain(list, p => p.Id == created.Id);
    }

    [Fact]
    public async Task PostRepository_WithGitUrl_CreatesRepositoryWithGitUrlMetadata()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "repo-giturl" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend", gitUrl = "git@example.com:backend.git", baseBranch = "main" });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.GetProperty("success").GetBoolean());
        var data = json.GetProperty("data");
        var repo = data.GetProperty("repositories").EnumerateArray().Single();
        Assert.Equal("backend", repo.GetProperty("name").GetString());
        Assert.Equal("git@example.com:backend.git", repo.GetProperty("gitUrl").GetString());
        Assert.Equal("main", repo.GetProperty("baseBranch").GetString());
        Assert.True(repo.GetProperty("isDefault").GetBoolean());
        Assert.False(repo.TryGetProperty("path", out _));
        Assert.False(repo.TryGetProperty("remote", out _));
        Assert.False(repo.TryGetProperty("resolvedPath", out _));
    }

    [Fact]
    public async Task PostRepository_WithoutGitUrl_ReturnsBadRequestAndDoesNotMutate()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "repo-pathonly" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend", path = "/proj/backend" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        Assert.Empty(repos);
    }

    [Fact]
    public async Task PatchRepository_UpdatesGitUrlAndBaseBranch()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "repo-update" });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend", gitUrl = "git@example.com:backend.git", baseBranch = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/backend",
            new { gitUrl = "git@example.com:backend-v2.git", baseBranch = "develop" });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var repo = json.GetProperty("data").GetProperty("repositories").EnumerateArray().Single();
        Assert.Equal("backend", repo.GetProperty("name").GetString());
        Assert.Equal("git@example.com:backend-v2.git", repo.GetProperty("gitUrl").GetString());
        Assert.Equal("develop", repo.GetProperty("baseBranch").GetString());
    }

    [Fact]
    public async Task PatchRepository_WithoutGitUrl_ReturnsBadRequestAndDoesNotMutate()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "repo-update-no-giturl" });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend", gitUrl = "git@example.com:backend.git", baseBranch = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/backend",
            new { path = "/proj/backend" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        var repo = repos.Single();
        Assert.Equal("git@example.com:backend.git", repo.GitUrl);
        Assert.Equal("main", repo.BaseBranch);
    }

    [Fact]
    public async Task GetRepositories_ListReturnsRepositoriesWithoutPathFields()
    {
        var created = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = "repo-list" });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend", gitUrl = "git@example.com:backend.git", baseBranch = "main" });

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        var repo = repos.Single();
        Assert.Equal("backend", repo.Name);
        Assert.Equal("git@example.com:backend.git", repo.GitUrl);
        Assert.Equal("main", repo.BaseBranch);
        Assert.True(repo.IsDefault);
    }

    private sealed record RepositoryInfoDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);
}
