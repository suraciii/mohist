using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public sealed class SessionTreeStopApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SessionTreeStopApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublicStopRejectsSnapshotInputs_ReplaysOperation_AndReadsTheDurableResource()
    {
        var projectId = await CreateProjectAsync("stop-api");
        var rootId = $"stop-root-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, rootId, "root-agent");
        var path = $"/api/projects/{projectId}/agent-sessions/{rootId}/stop";

        using (var invalid = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new
            {
                revision = 99,
                membership = new[] { "client-session" },
                targets = new[] { new { sessionId = "client-session" } },
            }),
        })
        {
            invalid.Headers.Add("Idempotency-Key", "stop-api-key");
            using var rejected = await _fixture.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal("stop_body_not_allowed", await ReadPropertyAsync(rejected, "code"));
        }

        var first = await PostStopAsync(path, "stop-api-key");
        var firstOperationId = first.GetProperty("operationId").GetString();
        Assert.Equal("completed", first.GetProperty("status").GetString());
        Assert.False(first.GetProperty("admissionFenceActive").GetBoolean());
        Assert.Equal(rootId, first.GetProperty("membership")[0].GetProperty("sessionId").GetString());

        var replay = await PostStopAsync(path, "stop-api-key");
        Assert.Equal(firstOperationId, replay.GetProperty("operationId").GetString());
        Assert.Equal(first.GetProperty("membership").GetRawText(), replay.GetProperty("membership").GetRawText());
        Assert.Equal(first.GetProperty("targets").GetRawText(), replay.GetProperty("targets").GetRawText());

        using var read = await _fixture.Client.GetAsync(
            $"{path}/{Uri.EscapeDataString(firstOperationId!)}");
        read.EnsureSuccessStatusCode();
        var readData = await ReadDataAsync(read);
        Assert.Equal(firstOperationId, readData.GetProperty("operationId").GetString());
        Assert.Equal(first.GetProperty("membership").GetRawText(), readData.GetProperty("membership").GetRawText());
    }

    [Fact]
    public async Task SameOperationKeyWithDifferentFingerprintReturnsConflict()
    {
        var projectId = await CreateProjectAsync("stop-conflict");
        var rootId = $"stop-conflict-root-{Guid.NewGuid():N}";
        var key = "stop-conflict-key";
        await OpenSessionAsync(projectId, rootId, "stop-conflict-agent");
        var operationId = SessionTreeStopOperationIds.For(projectId, rootId, key);
        var grain = _fixture.Grains.GetGrain<ISessionTreeStopOperationGrain>(operationId);
        var first = new SessionTreeStopRequest(
            projectId,
            rootId,
            operationId,
            key,
            "fingerprint-one");

        var started = await grain.StartAsync(first);
        Assert.Equal(first.OperationId, started.OperationId);
        var retry = await grain.StartAsync(first);
        Assert.Equal(first.OperationId, retry.OperationId);
        await Assert.ThrowsAsync<SessionTreeStopOperationConflictException>(() =>
            grain.StartAsync(first with { RequestFingerprint = "fingerprint-two" }));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{rootId}/stop");
        request.Headers.Add("Idempotency-Key", key);
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_conflict", await ReadPropertyAsync(response, "code"));
    }

    private async Task<JsonElement> PostStopAsync(string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Idempotency-Key", key);
        using var response = await _fixture.Client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Stop failed: {(int)response.StatusCode} {detail}");
        }
        return await ReadDataAsync(response);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(63, prefix.Length + 33)];
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task OpenSessionAsync(string projectId, string sessionId, string agentId)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "runner-stop-api",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = agentId,
                [GenericAgentSessionMetadata.AgentName] = agentId,
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-stop-api",
            ExpectedRunnerId: "runner-stop-api",
            ExpectedRuntime: "opencode"));
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data");
    }

    private static async Task<string?> ReadPropertyAsync(HttpResponseMessage response, string name)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
