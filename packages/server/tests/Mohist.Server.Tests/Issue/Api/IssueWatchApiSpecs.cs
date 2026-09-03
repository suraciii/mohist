using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Issue.Api;

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
[Trait("level", "L1")]
public class IssueWatchApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly HttpClient _client;

    private readonly MohistIntegrationFixture _fixture;

    public IssueWatchApiSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
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
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            name,
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }

    private const int ProjectDomainMaxLength = 63;

    private async Task<AgentRef> CreateAgentAsync(string projectId, string name)
    {
        var agentId = $"agent_{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, agentId)).CreateAsync(
            new AgentCreateData(
                projectId,
                name,
                $"description for {name}",
                $"instructions for {name}",
                JsonSerializer.SerializeToElement(new { model = "openai/gpt-5.6" }),
                new[] { "coding" },
                1));
        return new AgentRef(agentId, name);
    }

    private async Task<int> CreateIssueAsync(string projectId, string title)
    {
        var number = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        return await _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number))).CreateAsync(
            projectId,
            number,
            title,
            null,
            null,
            null,
            repositoryRef: "main",
            isDraft: true);
    }

    private sealed record AgentRef(string Id, string Name);
}
