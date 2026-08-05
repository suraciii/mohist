using System.Net;
using System.Text;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("IntegrationApi")]
public sealed class AgentSessionSpawnRouteSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSessionSpawnRouteSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SpawnRoute_PreservesPromptWhitespaceInExactIdempotencyFingerprint()
    {
        var projectId = await CreateProjectAsync();
        var path = $"/api/projects/{projectId}/agent-sessions/missing-parent/spawns";
        const string idempotencyKey = "spawn-whitespace-contract";

        using var first = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent("{\"targetAgentRef\":\"agent-target\",\"prompt\":\"foo \"}")
        };
        first.Headers.Add("Idempotency-Key", idempotencyKey);
        using var firstResponse = await _fixture.Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Conflict, firstResponse.StatusCode);
        Assert.Equal("spawn_rejected", await ReadCodeAsync(firstResponse));

        using var replay = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent("{\"targetAgentRef\":\"agent-target\",\"prompt\":\"foo\"}")
        };
        replay.Headers.Add("Idempotency-Key", idempotencyKey);
        using var replayResponse = await _fixture.Client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Conflict, replayResponse.StatusCode);
        Assert.Equal("spawn_idempotency_conflict", await ReadCodeAsync(replayResponse));
    }

    private async Task<string> CreateProjectAsync()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = $"spawn-route-{Guid.NewGuid():N}",
                repository = new
                {
                    name = "primary",
                    gitUrl = "git@example.com:primary.git",
                    baseBranch = "main",
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }
}
