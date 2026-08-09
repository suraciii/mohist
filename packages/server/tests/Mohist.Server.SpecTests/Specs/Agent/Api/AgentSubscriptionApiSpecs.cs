using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public sealed class AgentSubscriptionApiSpecs(MohistIntegrationFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task List_EmptyAgent_ReturnsCanonicalDataAndNoConnectionState()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-empty");

        using var response = await Client.GetAsync(Path(projectId, agentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("no_connection", data.GetProperty("state").GetString());
        Assert.Equal("Unknown", data.GetProperty("readiness").GetString());
        Assert.Equal("no_connection", data.GetProperty("connection").GetString());
        Assert.Empty(data.GetProperty("subscriptions").EnumerateArray());
    }

    [Fact]
    public async Task Create_ReplayAndDeleteAreIdempotent()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-idempotent");
        var path = Path(projectId, agentId);
        const string key = "subscription-create-retry";
        var body = new
        {
            name = "release",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
            @continue = false,
        };
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        firstRequest.Headers.Add("Idempotency-Key", key);

        using var first = await Client.SendAsync(firstRequest);
        var firstData = await ReadDataAsync(first);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        replayRequest.Headers.Add("Idempotency-Key", key);
        using var replay = await Client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayData = await ReadDataAsync(replay);
        Assert.Equal(firstData.GetProperty("id").GetString(), replayData.GetProperty("id").GetString());

        var list = await Client.GetDataAsync<JsonElement>(path);
        Assert.Single(list.GetProperty("subscriptions").EnumerateArray());

        var id = firstData.GetProperty("id").GetString()!;
        using var patched = await Client.PatchAsJsonAsync($"{path}/{id}", new { @continue = true });
        var patchedData = await ReadDataAsync(patched);
        Assert.True(patchedData.GetProperty("continue").GetBoolean());

        using var deleted = await Client.DeleteAsync($"{path}/{id}");
        using var repeatedDelete = await Client.DeleteAsync($"{path}/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeatedDelete.StatusCode);
        Assert.Equal("deleted", (await ReadDataAsync(deleted)).GetProperty("status").GetString());
        Assert.Equal("deleted", (await ReadDataAsync(repeatedDelete)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_ArchivedAgentReturnsExplicitConflict()
    {
        var (projectId, agentId) = await CreateProjectAndAgentAsync("subscription-archived");
        using var archive = await Client.DeleteAsync($"/api/projects/{projectId}/agents/{agentId}");
        archive.EnsureSuccessStatusCode();

        using var response = await Client.PostAsJsonAsync(Path(projectId, agentId), new
        {
            name = "archived-rule",
            match = "event.type == \"release\"",
            responsePrompt = "Summarize the release.",
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_archived", body.GetProperty("code").GetString());
    }

    private async Task<(string ProjectId, string AgentId)> CreateProjectAndAgentAsync(string prefix)
    {
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects", $"{prefix}-{Guid.NewGuid():N}");
        var agent = await Client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", new
        {
            name = "subscription-agent",
            description = "subscription spec agent",
            instructions = "subscription spec instructions",
            agentConfig = new { model = "openai/gpt-5.6", runtime = "pi" },
            skills = Array.Empty<string>(),
            maxConcurrentRuns = 1,
        });
        return (project.Id, agent.Id);
    }

    private static string Path(string projectId, string agentId) =>
        $"/api/projects/{projectId}/agents/{agentId}/subscriptions";

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        return envelope.GetProperty("data");
    }

    private sealed record ProjectDto(string Id);
    private sealed record AgentDto(string Id);
}
