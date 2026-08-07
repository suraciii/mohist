using System.Net;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubFeed")]
public sealed class GitHubConnectionUpdateSpecs
{
    private const string RepoName = "hello-world";

    private readonly GitHubFeedFixture _fixture;

    public GitHubConnectionUpdateSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
    }

    private HttpClient Client => _fixture.Client;

    [Fact]
    public async Task UpdateApprovers_ReturnsUpdatedConnection()
    {
        var (projectId, connectionId, owner) = await ConnectAsync(["alice"]);

        var updated = await Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/github-connections/{connectionId}",
            new { approvers = new[] { "bob", "alice" } });

        Assert.Equal(owner, updated.GetProperty("owner").GetString());
        Assert.Equal(RepoName, updated.GetProperty("repo").GetString());
        var approvers = updated.GetProperty("approvers").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal(new[] { "alice", "bob" }, approvers.Cast<string>().ToArray());
    }

    [Fact]
    public async Task UpdateApprovers_ClearList_ReturnsEmptyApprovers()
    {
        var (projectId, connectionId, _) = await ConnectAsync(["alice"]);

        var updated = await Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/github-connections/{connectionId}",
            new { approvers = Array.Empty<string>() });

        Assert.Empty(updated.GetProperty("approvers").EnumerateArray());
    }

    [Fact]
    public async Task UpdateApprovers_EmptyBody_DoesNotChangeList()
    {
        var (projectId, connectionId, _) = await ConnectAsync(["alice"]);

        var updated = await Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/github-connections/{connectionId}",
            new { });

        var approvers = updated.GetProperty("approvers").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal(new[] { "alice" }, approvers.Cast<string>().ToArray());
    }

    [Fact]
    public async Task UpdateApprovers_UnknownConnection_NotFound()
    {
        var (projectId, _, _) = await ConnectAsync(["alice"]);

        using var response = await Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/github-connections/ghconn_missing",
            new { approvers = new[] { "alice" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateApprovers_NullBody_BadRequest()
    {
        var (projectId, connectionId, _) = await ConnectAsync(["alice"]);

        using var response = await Client.PatchAsJsonAsync<object?>(
            $"/api/projects/{projectId}/github-connections/{connectionId}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(string ProjectId, string ConnectionId, string Owner)> ConnectAsync(string[] approvers)
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-update-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
            approvers,
        });
        return (project.Id, created.GetProperty("id").GetString()!, owner);
    }
}
