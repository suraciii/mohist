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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PostProject_WithoutRepository_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new { name = "no-default-repo" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("repository", json.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PostProject_WithoutRepositoryGitUrl_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "no-giturl",
            repository = new { name = "main", gitUrl = "" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PostProject_WithRepository_CreatesProjectWithOneDefaultRepository()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repository-backed",
                repository = new
                {
                    name = "main",
                    gitUrl = "git@example.com:main.git",
                    baseBranch = "main",
                },
            });

        Assert.Single(created.Repositories);
        var repo = created.Repositories[0];
        Assert.Equal("main", repo.Name);
        Assert.Equal("git@example.com:main.git", repo.GitUrl);
        Assert.True(repo.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetProjects_ListReturnsCreatedProject()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "list-test",
                repository = new
                {
                    name = "main",
                    gitUrl = "git@example.com:main.git",
                    baseBranch = "main",
                },
            });

        var list = await _client.GetDataAsync<List<ProjectInfo>>("/api/projects");
        var project = list.Single(p => p.Id == created.Id);
        Assert.Equal("list-test", project.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectUse_AndDelete_RemainFunctional()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "use-delete-test",
                repository = new
                {
                    name = "main",
                    gitUrl = "git@example.com:main.git",
                    baseBranch = "main",
                },
            });

        await _client.PostOkAsync($"/api/projects/{created.Id}/use");

        var fetched = await _client.GetDataAsync<ProjectInfo>($"/api/projects/{created.Id}");
        Assert.Equal("use-delete-test", fetched.Name);

        using var deleteResponse = await _client.DeleteAsync($"/api/projects/{created.Id}");
        deleteResponse.EnsureSuccessStatusCode();

        var list = await _client.GetDataAsync<List<ProjectInfo>>("/api/projects");
        Assert.DoesNotContain(list, p => p.Id == created.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PostRepository_WithGitUrl_AddsSecondRepositoryPreservingDefault()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-add",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend", gitUrl = "git@example.com:backend.git", baseBranch = "main" });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.GetProperty("success").GetBoolean());
        var repos = json.GetProperty("data").GetProperty("repositories").EnumerateArray().ToList();
        Assert.Equal(2, repos.Count);

        var server = repos.Single(r => r.GetProperty("name").GetString() == "server");
        var backend = repos.Single(r => r.GetProperty("name").GetString() == "backend");
        Assert.True(server.GetProperty("isDefault").GetBoolean());
        Assert.False(backend.GetProperty("isDefault").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PostRepository_DuplicateNameDifferentCase_ReturnsConflict()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "dup-repo-test",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "SERVER", gitUrl = "git@example.com:server-2.git", baseBranch = "main" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        Assert.Single(repos);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PostRepository_WithoutGitUrl_ReturnsBadRequestAndDoesNotMutate()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-add-no-url",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "backend" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        Assert.Single(repos);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PatchRepository_MetadataUpdate_PersistsNewGitUrlAndBaseBranch()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-update",
                repository = new
                {
                    name = "backend",
                    gitUrl = "git@example.com:backend.git",
                    baseBranch = "main",
                },
            });

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PatchRepository_EmptyUpdate_ReturnsBadRequestAndDoesNotMutate()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-update-empty",
                repository = new
                {
                    name = "backend",
                    gitUrl = "git@example.com:backend.git",
                    baseBranch = "main",
                },
            });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/backend",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        var repo = repos.Single();
        Assert.Equal("git@example.com:backend.git", repo.GitUrl);
        Assert.Equal("main", repo.BaseBranch);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PatchRepository_UnknownName_ReturnsNotFound()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-update-unknown",
                repository = new
                {
                    name = "backend",
                    gitUrl = "git@example.com:backend.git",
                    baseBranch = "main",
                },
            });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/ghost",
            new { gitUrl = "git@example.com:other.git" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PatchRepository_SetDefaultTrue_SwitchesDefault()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-set-default",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "web", gitUrl = "git@example.com:web.git", baseBranch = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/web",
            new { setDefault = true });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var repos = json.GetProperty("data").GetProperty("repositories").EnumerateArray().ToList();
        Assert.Equal(2, repos.Count);
        Assert.False(repos.Single(r => r.GetProperty("name").GetString() == "server").GetProperty("isDefault").GetBoolean());
        Assert.True(repos.Single(r => r.GetProperty("name").GetString() == "web").GetProperty("isDefault").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PatchRepository_SetDefaultOnCurrentDefault_IsIdempotent()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-idempotent",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });
        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/server",
            new { setDefault = true });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var repos = json.GetProperty("data").GetProperty("repositories").EnumerateArray().ToList();
        Assert.Single(repos);
        Assert.True(repos[0].GetProperty("isDefault").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task DeleteRepository_NonDefault_SucceedsAndKeepsDefault()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-delete",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "web", gitUrl = "git@example.com:web.git", baseBranch = "main" });

        using var response = await _client.DeleteAsync($"/api/projects/{created.Id}/repositories/web");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var repos = json.GetProperty("data").GetProperty("repositories").EnumerateArray().ToList();
        Assert.Single(repos);
        Assert.Equal("server", repos[0].GetProperty("name").GetString());
        Assert.True(repos[0].GetProperty("isDefault").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task DeleteRepository_Default_ReturnsConflictAndDoesNotMutate()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "repo-delete-default",
                repository = new
                {
                    name = "server",
                    gitUrl = "git@example.com:server.git",
                    baseBranch = "main",
                },
            });

        using var response = await _client.DeleteAsync($"/api/projects/{created.Id}/repositories/server");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("default", json.GetProperty("error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        Assert.Single(repos);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetProject_QueryModel_ReturnsFlaggedDefaultRepository()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = "query-default",
                repository = new
                {
                    name = "first-listed",
                    gitUrl = "git@example.com:first.git",
                    baseBranch = "main",
                },
            });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "default-one", gitUrl = "git@example.com:d.git", baseBranch = "main", setDefault = true });

        var fetched = await _client.GetDataAsync<ProjectInfo>($"/api/projects/{created.Id}");
        Assert.NotNull(fetched.DefaultRepository);
        Assert.Equal("default-one", fetched.DefaultRepository!.Name);
        Assert.True(fetched.DefaultRepository.IsDefault);
    }

    private sealed record RepositoryInfoDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);
}
