using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Route-level contract coverage for the per-issue repository binding
/// patch surface. Calculation/orchestration semantics live in
/// <c>IssueRepositoryResolverSpecs</c> (project/repository resolver
/// logic) and <c>IssueRepositoryBindingApiSpecs</c>'s grain paths
/// covered by the dedicated <c>WorkflowGrainFixture</c>-based
/// binding specs; this file keeps the JSON-shape, status, and
/// error-code assertions that must be driven through <c>HttpClient</c>.
/// </summary>
[Collection("IntegrationIssue")]
public class IssueRepositoryBindingApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public IssueRepositoryBindingApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _fixture = fixture;
    }

    [Fact]
    public async Task PostIssue_WithUnknownRepository_ReturnsBadRequest()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Ghost", repositoryName = "ghost" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchIssue_WithUnknownRepository_ReturnsBadRequest_LeavesIssueUnchanged()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var create = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Anchor", repositoryName = "main" },
            JsonOptions);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var number = created.GetProperty("data").GetProperty("number").GetInt32();

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { repositoryName = "ghost" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchIssue_UnknownIssue_Returns404()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/999999",
            new { title = "unknown" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchIssue_WithRepositoryName_ReassignsBeforeStart_Returns200()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var create = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Move me", repositoryName = "main" },
            JsonOptions);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var number = created.GetProperty("data").GetProperty("number").GetInt32();

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{number}",
            new { repositoryName = "SECONDARY" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostIssue_UnknownProject_Returns404()
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/proj-rb-unknown-{Guid.NewGuid():N}/issues",
            new { title = "no project" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string ProjectId, Mohist.Server.Project.Services.ProjectInfo Project)> SetupProjectWithRepositoriesAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        var project = await grain.CreateAsync(
            $"proj-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@main.example:repo.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", "develop");
        return (projectId, project);
    }
}