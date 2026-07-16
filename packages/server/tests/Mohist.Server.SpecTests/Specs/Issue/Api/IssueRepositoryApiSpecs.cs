using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssueRepositoryApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueRepositoryApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task PostIssue_WithExplicitRepositoryName_ResponseIncludesResolvedRepositoryFromProjectConfig()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        var envelope = await CreateIssueAsync(projectId, new
        {
            title = "Explicit repo",
            repositoryName = "secondary",
        });

        Assert.NotNull(envelope.Data?.Repository);
        Assert.Equal("secondary", envelope.Data!.Repository!.Name);
        Assert.Equal("git@secondary.example:repo.git", envelope.Data.Repository.GitUrl);
        Assert.Equal("develop", envelope.Data.Repository.BaseBranch);
        Assert.False(envelope.Data.Repository.IsDefault);
        Assert.Null(envelope.Data.RepositoryProblem);
    }

    [Fact]
    public async Task PostIssue_WithoutRepositoryName_ResponseIncludesResolvedDefaultRepositoryFromProjectConfig()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        var envelope = await CreateIssueAsync(projectId, new { title = "Default repo issue" });

        Assert.NotNull(envelope.Data?.Repository);
        Assert.Equal("main", envelope.Data!.Repository!.Name);
        Assert.Equal("git@main.example:repo.git", envelope.Data.Repository.GitUrl);
        Assert.Equal("main", envelope.Data.Repository.BaseBranch);
        Assert.True(envelope.Data.Repository.IsDefault);
        Assert.Null(envelope.Data.RepositoryProblem);
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
        var envelope = await response.Content.ReadFromJsonAsync<RepositoryApiEnvelope<IssueRepositoryDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("repository_not_found", envelope.Code);
        Assert.Contains("ghost", envelope.Error ?? string.Empty);
    }

    [Fact]
    public async Task GetIssue_AfterProjectChangesDefaultRepository_ReturnsNewlyResolvedRepositoryContext()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var createdBeforeSwap = await CreateIssueAsync(projectId, new { title = "Drifts when default repository changes" });
        Assert.Equal("main", createdBeforeSwap.Data!.Repository!.Name);
        Assert.True(createdBeforeSwap.Data.Repository.IsDefault);

        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).SetDefaultRepositoryAsync("secondary");

        var fetchedExistingIssue = await GetIssueAsync(projectId, createdBeforeSwap.Data.Number);
        Assert.NotNull(fetchedExistingIssue);
        Assert.NotNull(fetchedExistingIssue!.Repository);
        Assert.Equal("main", fetchedExistingIssue.Repository!.Name);
        Assert.Equal("git@main.example:repo.git", fetchedExistingIssue.Repository.GitUrl);
        Assert.False(fetchedExistingIssue.Repository.IsDefault);

        var createdAfterSwap = await CreateIssueAsync(projectId, new { title = "Picks up new default" });
        var fetchedNewIssue = await GetIssueAsync(projectId, createdAfterSwap.Data!.Number);
        Assert.NotNull(fetchedNewIssue);
        Assert.NotNull(fetchedNewIssue!.Repository);
        Assert.Equal("secondary", fetchedNewIssue.Repository!.Name);
        Assert.True(fetchedNewIssue.Repository.IsDefault);
    }

    [Fact]
    public async Task GetIssue_AfterRepositoryMetadataChange_ReturnsResolvedGitUrlAndBaseBranchFromCurrentProjectConfig()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Reflects new metadata", repositoryName = "secondary" });
        Assert.Equal("git@secondary.example:repo.git", created.Data!.Repository!.GitUrl);
        Assert.Equal("develop", created.Data.Repository.BaseBranch);

        // Drive the issue to terminal so the repository deletion guard
        // (issue-417 T-004) does not block the remove-and-re-add
        // exercise.
        await _fixture.Grains
            .GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(created.Data!.Id)
            .CancelAsync();

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release");

        var fetched = await GetIssueAsync(projectId, created.Data.Number);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched!.Repository);
        Assert.Equal("secondary", fetched.Repository!.Name);
        Assert.Equal("git@secondary.example:repo-new.git", fetched.Repository.GitUrl);
        Assert.Equal("release", fetched.Repository.BaseBranch);
        Assert.Null(fetched.RepositoryProblem);
    }

    [Fact]
    public async Task GetIssues_AfterRepositoryMetadataChange_ReturnsCurrentRepositoryContext()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Listed repository", repositoryName = "secondary" });

        await _fixture.Grains
            .GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(created.Data!.Id)
            .CancelAsync();

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release");

        var listed = await GetIssuesAsync(projectId);
        var issue = Assert.Single(listed, item => item.Number == created.Data!.Number);

        Assert.NotNull(issue.Repository);
        Assert.Equal("secondary", issue.Repository!.Name);
        Assert.Equal("git@secondary.example:repo-new.git", issue.Repository.GitUrl);
        Assert.Equal("release", issue.Repository.BaseBranch);
        Assert.Null(issue.RepositoryProblem);
    }

    [Fact]
    public async Task GetIssue_AfterReferencedRepositoryRemoved_ReturnsRepositoryProblemInsteadOfFallbackRepository()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Orphaned by repo removal", repositoryName = "secondary" });
        Assert.Equal("secondary", created.Data!.Repository!.Name);

        await _fixture.Grains
            .GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(created.Data!.Id)
            .CancelAsync();
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        var fetched = await GetIssueAsync(projectId, created.Data.Number);

        Assert.NotNull(fetched);
        Assert.Null(fetched!.Repository);
        Assert.NotNull(fetched.RepositoryProblem);
        Assert.Equal("repositoryNotFound", fetched.RepositoryProblem!.Code);
        Assert.Equal("secondary", fetched.RepositoryProblem.RepositoryRef);
        Assert.NotNull(fetched.RepositoryProblem.CandidateNames);
        Assert.Contains("main", fetched.RepositoryProblem.CandidateNames!);
        Assert.DoesNotContain("secondary", fetched.RepositoryProblem.CandidateNames!);
    }

    private async Task<RepositoryApiEnvelope<IssueRepositoryDto>> CreateIssueAsync(string projectId, object body)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            body,
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<RepositoryApiEnvelope<IssueRepositoryDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        Assert.NotNull(envelope.Data);
        return envelope;
    }

    private async Task<IssueRepositoryDto?> GetIssueAsync(string projectId, int number)
    {
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{number}");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<RepositoryApiEnvelope<IssueRepositoryDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        return envelope.Data;
    }

    private async Task<IssueRepositoryDto[]> GetIssuesAsync(string projectId)
    {
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<RepositoryApiEnvelope<IssueRepositoryDto[]>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        Assert.NotNull(envelope.Data);
        return envelope.Data;
    }

    private async Task<(string ProjectId, ProjectInfo Project)> SetupProjectWithRepositoriesAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        var project = await grain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "main", GitUrl = "git@main.example:repo.git", BaseBranch = "main", IsDefault = true });
        await grain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", "develop");
        return (projectId, project);
    }

    private sealed record RepositoryApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null);

    private sealed record IssueRepositoryDto(
        int Number,
        string Id,
        RepositoryDto? Repository,
        RepositoryProblemDto? RepositoryProblem);

    private sealed record RepositoryDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);

    private sealed record RepositoryProblemDto(string Code, string Message, string? RepositoryRef, string[]? CandidateNames);
}
