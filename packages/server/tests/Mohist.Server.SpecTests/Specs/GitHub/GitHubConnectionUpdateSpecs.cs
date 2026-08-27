using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubCommand")]
public sealed class GitHubConnectionUpdateSpecs
{
    private const string RepoName = "hello-world";

    private readonly GitHubCommandFixture _fixture;

    public GitHubConnectionUpdateSpecs(GitHubCommandFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
    }

    private HttpClient Client => _fixture.Client;

    [Fact]
    public async Task Create_WithoutPat_RejectsBeforeCreatingConnection()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-credential-required-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");

        using var response = await Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepoName });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("pat_required", body.GetProperty("code").GetString());
        using var list = await Client.GetAsync($"/api/projects/{project.Id}/github-connections");
        var listBody = JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync());
        Assert.Empty(listBody.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Create_WithPat_StoresCredentialForProductionPort()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-credential-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepoName, pat = "github-pat" });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var stored = await secrets.LoadAsync(
            GitHubConnectionStore.ApiSecretAddress(project.Id, created.GetProperty("id").GetString()!));

        Assert.Equal("github-pat", Encoding.UTF8.GetString(stored!));
        Assert.Equal("pat", created.GetProperty("identityKind").GetString());
        Assert.False(created.TryGetProperty("pat", out _));
    }

    [Fact]
    public async Task Enable_WhenPatWasDeleted_ReturnsStableConflictAndRemainsDisabled()
    {
        var (projectId, connectionId, _) = await ConnectAsync([]);
        var connectionPath = $"/api/projects/{projectId}/github-connections/{connectionId}";

        using (var disable = await Client.PostAsync($"{connectionPath}/disable", content: null))
            disable.EnsureSuccessStatusCode();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            await secrets.DeleteAsync(GitHubConnectionStore.ApiSecretAddress(projectId, connectionId));
        }

        using var response = await Client.PostAsync($"{connectionPath}/enable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("pat_required", body.GetProperty("code").GetString());

        using var current = await Client.GetAsync(connectionPath);
        var currentBody = JsonSerializer.Deserialize<JsonElement>(await current.Content.ReadAsStringAsync());
        Assert.Equal("disabled", currentBody.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Enable_WhenPatIsBlank_ReturnsStableConflictAndRemainsDisabled()
    {
        var (projectId, connectionId, _) = await ConnectAsync([]);
        var connectionPath = $"/api/projects/{projectId}/github-connections/{connectionId}";

        using (var disable = await Client.PostAsync($"{connectionPath}/disable", content: null))
            disable.EnsureSuccessStatusCode();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            await secrets.StoreAsync(
                GitHubConnectionStore.ApiSecretAddress(projectId, connectionId),
                Encoding.UTF8.GetBytes(" \t\r\n"));
        }

        using var response = await Client.PostAsync($"{connectionPath}/enable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("pat_required", body.GetProperty("code").GetString());

        using var current = await Client.GetAsync(connectionPath);
        var currentBody = JsonSerializer.Deserialize<JsonElement>(await current.Content.ReadAsStringAsync());
        Assert.Equal("disabled", currentBody.GetProperty("data").GetProperty("status").GetString());
    }

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
            pat = "github-pat",
        });
        return (project.Id, created.GetProperty("id").GetString()!, owner);
    }
}
