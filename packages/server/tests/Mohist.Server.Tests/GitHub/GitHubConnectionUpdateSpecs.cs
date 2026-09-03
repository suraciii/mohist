using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.TestSupport;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.Tests.GitHub;

[Collection("GitHubCommand")]
[Trait("level", "L1")]
public sealed class GitHubConnectionUpdateSpecs
{
    private const string RepoName = "hello-world";
    private readonly GitHubCommandFixture _fixture;

    public GitHubConnectionUpdateSpecs(GitHubCommandFixture fixture) => _fixture = fixture;
    private HttpClient Client => _fixture.Client;

    [Fact]
    public async Task Create_UsesAppInstallationAndReturnsIdentity()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-app-{Guid.NewGuid():N}", repoName: RepoName,
            gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepoName });

        Assert.Equal("installation-test", created.GetProperty("installationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("repositoryNodeId").GetString()));
        Assert.False(created.GetProperty("reconnectRequired").GetBoolean());
        Assert.False(created.GetProperty("needsAttention").GetBoolean());
        Assert.True(created.GetProperty("webhookSecret").GetString()!.Length > 0);
    }

    [Fact]
    public async Task Create_WithPatOption_IsRejected()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-app-pat-{Guid.NewGuid():N}", repoName: RepoName,
            gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        using var response = await Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepoName, pat = "not-accepted" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_option", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_WhenAppInstallationIsMissing_ReturnsInstallGuidance()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-app-missing-{Guid.NewGuid():N}", repoName: RepoName,
            gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var app = _fixture.Services.GetRequiredService<FakeGitHubAppClient>();
        app.InstallationMissing = true;
        try
        {
            using var response = await Client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepoName });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("github_app_installation_required", body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("details").GetProperty("installationUrl").GetString()!.Length > 0);
        }
        finally
        {
            app.InstallationMissing = false;
        }
    }

    [Fact]
    public async Task Create_WhenAppPermissionIsDenied_ReturnsActionableErrorWithoutInstallUrl()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-app-permission-{Guid.NewGuid():N}", repoName: RepoName,
            gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var app = _fixture.Services.GetRequiredService<FakeGitHubAppClient>();
        app.DiscoveryFailure = new GitHubAppInstallationException(
            "The GitHub App cannot access this Repository. Update the App Repository scope, then retry.",
            "github_app_permission_denied");
        try
        {
            using var response = await Client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepoName });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("github_app_permission_denied", body.GetProperty("code").GetString());
            Assert.DoesNotContain("installationUrl", body.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            app.DiscoveryFailure = null;
        }
    }

    [Fact]
    public async Task UpdateApprovers_ReturnsUpdatedConnection()
    {
        var (projectId, connectionId, owner) = await ConnectAsync(["alice"]);
        var updated = await Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/github-connections/{connectionId}", new { approvers = new[] { "bob", "alice" } });
        Assert.Equal(owner, updated.GetProperty("owner").GetString());
        Assert.Equal(["alice", "bob"], updated.GetProperty("approvers").EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    [Fact]
    public async Task UpdateApprovers_ClearList_ReturnsEmptyApprovers()
    {
        var (projectId, connectionId, _) = await ConnectAsync(["alice"]);
        var updated = await Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/github-connections/{connectionId}", new { approvers = Array.Empty<string>() });
        Assert.Empty(updated.GetProperty("approvers").EnumerateArray());
    }

    [Fact]
    public async Task UpdateApprovers_EmptyBody_DoesNotChangeList()
    {
        var (projectId, connectionId, _) = await ConnectAsync(["alice"]);
        var updated = await Client.PatchDataAsync<JsonElement>(
            $"/api/projects/{projectId}/github-connections/{connectionId}", new { });
        Assert.Equal(["alice"], updated.GetProperty("approvers").EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    [Fact]
    public async Task UpdateApprovers_UnknownConnection_NotFound()
    {
        var (projectId, _, _) = await ConnectAsync(["alice"]);
        using var response = await Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/github-connections/ghconn_missing", new { approvers = new[] { "alice" } });
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
            "/api/projects", $"github-update-{Guid.NewGuid():N}", repoName: RepoName,
            gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections", new { owner, repo = RepoName, approvers });
        return (project.Id, created.GetProperty("id").GetString()!, owner);
    }
}
