using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Route-level contract coverage for the per-issue Agent watch surface.
/// State-machine semantics live in
/// <c>IssueWatchServiceSpecs</c> (driven by <c>MohistDbFixture</c>); this
/// file keeps the route-shape and error-code assertions that must be
/// driven through <c>HttpClient</c>:
/// <list type="bullet">
///   <item>404 on unknown project / unknown issue / unknown agent</item>
///   <item>409 <c>agent_archived</c> code on archived agent</item>
///   <item>200 + re-enriched detail JSON shape for the basic success path</item>
/// </list>
/// </summary>
[Collection("IntegrationIssue")]
public class IssueWatchApiSpecs
{
    private readonly HttpClient _client;

    public IssueWatchApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task PostWatch_OnNonexistentIssue_ReturnsNotFound()
    {
        var (projectId, _, agent) = await SeedAsync("watch-missing-issue");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/999999/watch",
            new { agentId = agent.Id });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostWatch_ForUnknownAgent_ReturnsAgentNotFound()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-unknown-agent");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/watch",
            new { agentId = "agent_does_not_exist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeleteWatch_ForUnknownAgent_ReturnsAgentNotFound()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-unknown-agent-delete");

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{projectId}/issues/{issueNumber}/watch")
        {
            Content = JsonContent.Create(new { agentId = "agent_does_not_exist" }),
        };
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PostWatch_ForArchivedAgent_ReturnsAgentArchived()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-archived-agent");
        var archivedAgent = await CreateAgentAsync(projectId, $"archived-{Guid.NewGuid():N}");
        using var archiveResponse = await _client.DeleteAsync(
            $"/api/projects/{projectId}/agents/{archivedAgent.Id}");
        archiveResponse.EnsureSuccessStatusCode();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/watch",
            new { agentId = archivedAgent.Id });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_archived", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PostWatch_AndGetIssueDetail_JsonShape_ExposesWatchingAndMutedArrays()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-shape");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/watch",
            new { agentId = agent.Id });
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Array, data.GetProperty("watching").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("muted").ValueKind);
        Assert.Equal(1, data.GetProperty("watching").GetArrayLength());
        Assert.Equal(0, data.GetProperty("muted").GetArrayLength());
    }

    private async Task<(string projectId, int issueNumber, AgentRef agent)> SeedAsync(string purpose)
    {
        var projectId = await CreateProjectAsync(purpose);
        var agent = await CreateAgentAsync(projectId, $"agent-{purpose}");
        var issueNumber = await CreateIssueAsync(projectId, $"issue-{purpose}");
        return (projectId, issueNumber, agent);
    }

    private async Task<string> CreateProjectAsync(string purpose)
    {
        var raw = $"watch-{purpose}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > ProjectDomainMaxLength ? raw[..ProjectDomainMaxLength] : raw;
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private const int ProjectDomainMaxLength = 63;

    private async Task<AgentRef> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private async Task<int> CreateIssueAsync(string projectId, string title)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title, isDraft = true });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("number").GetInt32();
    }

    private sealed record AgentRef(string Id, string Name);
}