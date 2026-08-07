using System.Net;
using System.Text;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
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

    [Fact]
    public async Task SpawnRoute_WorkspaceModeRetired_RejectsWithRetiredCode()
    {
        var projectId = await CreateProjectAsync();
        var path = $"/api/projects/{projectId}/agent-sessions/missing-parent/spawns";

        foreach (var value in new[] { "worktree", "inherit" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent($"{{\"targetAgentRef\":\"agent-target\",\"prompt\":\"foo\",\"workspace\":\"{value}\"}}")
            };
            request.Headers.Add("Idempotency-Key", $"spawn-retired-{value}");
            using var response = await _fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("workspace_mode_retired", await ReadCodeAsync(response));
        }
    }

    [Fact]
    public async Task TreeRoute_ReturnsApiEnvelopeWithLockedTreePageShape()
    {
        var projectId = await CreateProjectAsync();
        var rootSessionId = $"tree-route-root-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(rootSessionId).OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: "opencode",
                WorkDir: "/workspace",
                Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-tree-root",
                    [GenericAgentSessionMetadata.AgentName] = "agent-tree-root",
                })));

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-sessions/{rootSessionId}/tree?limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var envelope = document.RootElement;
        Assert.True(envelope.GetProperty("success").GetBoolean());
        var data = envelope.GetProperty("data");
        Assert.Equal(
            ["root", "revision", "nodes", "edges", "continuation"],
            data.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(rootSessionId, data.GetProperty("root").GetProperty("sessionId").GetString());
        Assert.Equal(0, data.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("nodes").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("edges").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("continuation").ValueKind);
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
