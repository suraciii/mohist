using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Route contract for the issue/epic → agent-session association
/// endpoints (<c>GET /api/projects/{projectRef}/issues/{n}/agent-sessions</c>
/// and <c>GET .../epics/{n}/agent-sessions</c>). The lookup rules
/// (issue-number label match, epic-number label match, project
/// scoping, agent-launch source filter) live in
/// <c>IssueSessionAssociationQuerierSpecs</c>.
/// </summary>
[Collection("IntegrationSessions")]
public class AgentSessionContextAssociationApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSessionContextAssociationApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task IssueAssociation_EmptyList_Returns200WithEmptyArray()
    {
        var project = await CreateProjectAsync($"issue-empty-{Guid.NewGuid():N}");
        var issueNumber = await CreateIssueAsync(project, "Empty issue");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/issues/{issueNumber}/agent-sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
        Assert.Empty(body.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task IssueAssociation_UnknownIssueNumber_Returns404()
    {
        var project = await CreateProjectAsync($"issue-404-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/issues/9999/agent-sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EpicAssociation_EmptyList_Returns200WithEmptyArray()
    {
        var project = await CreateProjectAsync($"epic-empty-{Guid.NewGuid():N}");
        var epic = await CreateEpicAsync(project, "Empty epic");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/epics/{epic}/agent-sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
        Assert.Empty(body.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task EpicAssociation_UnknownEpicRef_Returns404()
    {
        var project = await CreateProjectAsync($"epic-404-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/epics/9999/agent-sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<int> CreateIssueAsync(string projectId, string title)
    {
        var number = await _fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueCounterGrain>(projectId).NextAsync();
        var grain = _fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(new Mohist.Server.Infrastructure.Orleans.IssueKey(projectId, number)));
        await grain.CreateAsync(projectId, number, title, body: null, labels: null, priority: null, repositoryRef: null, risk: null, isDraft: true);
        return number;
    }

    private async Task<int> CreateEpicAsync(string projectId, string title)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/epics",
            new { title, description = "test epic", priority = "p2" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("number").GetInt32();
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }
}