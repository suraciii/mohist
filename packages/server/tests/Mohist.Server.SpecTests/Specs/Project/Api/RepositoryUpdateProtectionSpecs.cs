using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Api;

public class RepositoryUpdateProtectionSpecs
{
    private readonly HttpClient _client;

    public RepositoryUpdateProtectionSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task UpdateRepository_BlockedByNonTerminalIssue_ReturnsInUseConflict()
    {
        var created = await _client.PostDataAsync<ProjectInfo>(
            "/api/projects",
            new
            {
                name = $"repo-update-blocked-{Guid.NewGuid():N}",
                repository = new { name = "server", gitUrl = "git@example.com:server.git", baseBranch = "main" },
            });
        await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/repositories",
            new { name = "web", gitUrl = "git@example.com:web.git", baseBranch = "main" });
        using var issue = await _client.PostAsJsonAsync(
            $"/api/projects/{created.Id}/issues",
            new { title = "Blocker", repositoryName = "web" });
        issue.EnsureSuccessStatusCode();

        using var update = await _client.PatchAsJsonAsync(
            $"/api/projects/{created.Id}/repositories/web",
            new { gitUrl = "git@example.com:web-next.git" });

        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        var payload = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_in_use", payload.GetProperty("code").GetString());

        var repos = await _client.GetDataAsync<List<RepositoryInfoDto>>($"/api/projects/{created.Id}/repositories");
        Assert.Equal("git@example.com:web.git", repos.Single(r => r.Name == "web").GitUrl);
    }

    private sealed record RepositoryInfoDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);
}
