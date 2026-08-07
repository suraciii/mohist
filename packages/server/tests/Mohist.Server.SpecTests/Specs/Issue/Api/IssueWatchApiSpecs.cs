using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Locks the operate + read surface for the per-issue Agent watch
/// declarations: <c>POST /watch</c> + <c>DELETE /watch</c> drive the
/// state machine in <see cref="Mohist.Server.Agent.Services.WatchEntryStore"/>
/// and return the re-enriched <c>IssueReadModel</c>; <c>GET /issues/{n}</c>
/// and the list endpoint project <c>Watching</c> / <c>Muted</c> from the
/// shared <c>MohistDbContext</c>. Validation rejects archived / unknown
/// agents with their machine codes (<c>agent_not_found</c>,
/// <c>agent_archived</c>) and mutates no state.
///
/// Spec: <c>openspec/changes/issue-489/specs/issue-watch/spec.md</c>.
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
    public async Task PostWatch_WithNoPriorDeclaration_ReturnsReenrichedIssueWithAgentInWatching()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-add-none");

        var detail = await PostWatchAsync(projectId, issueNumber, agent.Id);

        Assert.Equal(issueNumber, detail.Number);
        Assert.Single(detail.Watching, entry => entry.AgentId == agent.Id && entry.State == "watching");
        Assert.Empty(detail.Muted);
    }

    [Fact]
    public async Task PostWatch_OnMutedDeclaration_TransitionsToWatching()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-unmute");
        await DeleteWatchAsync(projectId, issueNumber, agent.Id);

        var detail = await PostWatchAsync(projectId, issueNumber, agent.Id);

        Assert.Single(detail.Watching, entry => entry.AgentId == agent.Id && entry.State == "watching");
        Assert.Empty(detail.Muted);
    }

    [Fact]
    public async Task PostWatch_OnExistingWatching_IsIdempotent()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-add-idempotent");
        var first = await PostWatchAsync(projectId, issueNumber, agent.Id);
        var second = await PostWatchAsync(projectId, issueNumber, agent.Id);

        Assert.Single(first.Watching, entry => entry.AgentId == agent.Id);
        Assert.Single(second.Watching, entry => entry.AgentId == agent.Id);
        Assert.Equal(first.Watching[0].CreatedAt, second.Watching[0].CreatedAt);
    }

    [Fact]
    public async Task DeleteWatch_OnWatchingDeclaration_RemovesEntry()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-remove-watching");
        await PostWatchAsync(projectId, issueNumber, agent.Id);

        var detail = await DeleteWatchAsync(projectId, issueNumber, agent.Id);

        Assert.Empty(detail.Watching);
        Assert.Empty(detail.Muted);
    }

    [Fact]
    public async Task DeleteWatch_WithNoPriorDeclaration_RecordsMuted()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-remove-none");

        var detail = await DeleteWatchAsync(projectId, issueNumber, agent.Id);

        Assert.Empty(detail.Watching);
        Assert.Single(detail.Muted, entry => entry.AgentId == agent.Id && entry.State == "muted");
    }

    [Fact]
    public async Task DeleteWatch_OnExistingMuted_IsIdempotent()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-remove-muted");
        var first = await DeleteWatchAsync(projectId, issueNumber, agent.Id);
        var second = await DeleteWatchAsync(projectId, issueNumber, agent.Id);

        Assert.Single(first.Muted, entry => entry.AgentId == agent.Id);
        Assert.Single(second.Muted, entry => entry.AgentId == agent.Id);
        Assert.Equal(first.Muted[0].CreatedAt, second.Muted[0].CreatedAt);
    }

    [Fact]
    public async Task PostWatch_ForUnknownAgent_ReturnsAgentNotFoundWithoutMutatingState()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-unknown-agent");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/watch",
            new { agentId = "agent_does_not_exist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_not_found", error.GetProperty("code").GetString());

        var detail = await GetIssueDetailAsync(projectId, issueNumber);
        Assert.Empty(detail.Watching);
        Assert.Empty(detail.Muted);
    }

    [Fact]
    public async Task DeleteWatch_ForUnknownAgent_ReturnsAgentNotFoundWithoutMutatingState()
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

        var detail = await GetIssueDetailAsync(projectId, issueNumber);
        Assert.Empty(detail.Watching);
        Assert.Empty(detail.Muted);
    }

    [Fact]
    public async Task PostWatch_ForArchivedAgent_ReturnsAgentArchivedWithoutMutatingState()
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

        var detail = await GetIssueDetailAsync(projectId, issueNumber);
        Assert.Empty(detail.Watching);
        Assert.Empty(detail.Muted);
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
    public async Task GetIssueDetail_WithMixedEntries_ProjectsWatchingAndMutedAsSeparateGroups()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-detail-projection");
        var watchingA = await CreateAgentAsync(projectId, $"watch-a-{Guid.NewGuid():N}");
        var watchingB = await CreateAgentAsync(projectId, $"watch-b-{Guid.NewGuid():N}");
        var muted = await CreateAgentAsync(projectId, $"muted-{Guid.NewGuid():N}");

        await PostWatchAsync(projectId, issueNumber, watchingA.Id);
        await PostWatchAsync(projectId, issueNumber, watchingB.Id);
        await DeleteWatchAsync(projectId, issueNumber, muted.Id);

        var detail = await GetIssueDetailAsync(projectId, issueNumber);

        Assert.Equal(2, detail.Watching.Length);
        Assert.Single(detail.Muted);
        Assert.Contains(detail.Watching, entry => entry.AgentId == watchingA.Id);
        Assert.Contains(detail.Watching, entry => entry.AgentId == watchingB.Id);
        Assert.Contains(detail.Muted, entry => entry.AgentId == muted.Id);
        Assert.Contains(detail.Watching[0].State, new[] { "watching" });
        Assert.Equal("muted", detail.Muted[0].State);
    }

    [Fact]
    public async Task GetIssueDetail_WithNoEntries_ReturnsEmptyArrays()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-detail-empty");

        var detail = await GetIssueDetailAsync(projectId, issueNumber);

        Assert.NotNull(detail.Watching);
        Assert.NotNull(detail.Muted);
        Assert.Empty(detail.Watching);
        Assert.Empty(detail.Muted);
    }

    [Fact]
    public async Task ListIssues_WithWatchingEntries_ProjectsWatchingAndMutedOnEachItem()
    {
        var (projectId, issueNumber, agent) = await SeedAsync("watch-list-projection");
        await PostWatchAsync(projectId, issueNumber, agent.Id);

        var list = await _client.GetDataAsync<IssueDetailDto[]>($"/api/projects/{projectId}/issues?all=true");

        var item = Assert.Single(list, dto => dto.Number == issueNumber);
        Assert.Single(item.Watching, entry => entry.AgentId == agent.Id);
        Assert.Empty(item.Muted);
    }

    [Fact]
    public async Task ListIssues_WithNoEntries_ReturnsEmptyArraysPerItem()
    {
        var (projectId, issueNumber, _) = await SeedAsync("watch-list-empty");

        var list = await _client.GetDataAsync<IssueDetailDto[]>($"/api/projects/{projectId}/issues?all=true");

        var item = Assert.Single(list, dto => dto.Number == issueNumber);
        Assert.Empty(item.Watching);
        Assert.Empty(item.Muted);
    }

    private async Task<IssueDetailDto> PostWatchAsync(string projectId, int issueNumber, string agentId)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/watch",
            new { agentId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return IssueDetailDto.FromElement(body.GetProperty("data"));
    }

    private async Task<IssueDetailDto> DeleteWatchAsync(string projectId, int issueNumber, string agentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{projectId}/issues/{issueNumber}/watch")
        {
            Content = JsonContent.Create(new { agentId }),
        };
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return IssueDetailDto.FromElement(body.GetProperty("data"));
    }

    private async Task<IssueDetailDto> GetIssueDetailAsync(string projectId, int issueNumber)
    {
        var raw = await _client.GetRawAsync($"/api/projects/{projectId}/issues/{issueNumber}");
        using var doc = JsonDocument.Parse(raw);
        return IssueDetailDto.FromElement(doc.RootElement.GetProperty("data"));
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

    private sealed record IssueDetailDto(
        int Number,
        string Title,
        WatchEntryDto[] Watching,
        WatchEntryDto[] Muted)
    {
        public static IssueDetailDto FromElement(JsonElement element) => new(
            element.GetProperty("number").GetInt32(),
            element.GetProperty("title").GetString() ?? string.Empty,
            ParseWatching(element),
            ParseMuted(element));

        private static WatchEntryDto[] ParseWatching(JsonElement element) =>
            element.TryGetProperty("watching", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                    .Select(item => new WatchEntryDto(
                        item.GetProperty("agentId").GetString() ?? string.Empty,
                        item.GetProperty("state").GetString() ?? string.Empty,
                        item.GetProperty("createdAt").GetString() ?? string.Empty,
                        item.GetProperty("updatedAt").GetString() ?? string.Empty))
                    .ToArray()
                : throw new InvalidOperationException("watching field missing from issue detail");
        private static WatchEntryDto[] ParseMuted(JsonElement element) =>
            element.TryGetProperty("muted", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                    .Select(item => new WatchEntryDto(
                        item.GetProperty("agentId").GetString() ?? string.Empty,
                        item.GetProperty("state").GetString() ?? string.Empty,
                        item.GetProperty("createdAt").GetString() ?? string.Empty,
                        item.GetProperty("updatedAt").GetString() ?? string.Empty))
                    .ToArray()
                : throw new InvalidOperationException("muted field missing from issue detail");
    }

    private sealed record WatchEntryDto(string AgentId, string State, string CreatedAt, string UpdatedAt);
}
