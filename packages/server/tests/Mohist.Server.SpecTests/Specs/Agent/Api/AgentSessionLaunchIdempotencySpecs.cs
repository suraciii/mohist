using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentSessionLaunchIdempotencySpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchIdempotencySpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_ReplayAfterAgentRename_ReturnsOriginalLaunch()
    {
        var projectId = await CreateProjectAsync("launch-replay-renamed-agent");
        var agent = await CreateAgentAsync(projectId, "original-agent-name");
        const string idempotencyKey = "replay-after-agent-rename";

        using var first = await LaunchAsync(
            projectId,
            "original-agent-name",
            new { prompt = "preserve original launch" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await LaunchReferencesAsync(first);

        using var rename = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}",
            new { name = "renamed-agent" });
        rename.EnsureSuccessStatusCode();

        using var replay = await LaunchAsync(
            projectId,
            "original-agent-name",
            new { prompt = "preserve original launch" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await LaunchReferencesAsync(replay));
    }

    [Fact]
    public async Task Launch_ReplayAfterAgentArchive_ReturnsOriginalLaunch()
    {
        var projectId = await CreateProjectAsync("launch-replay-archived-agent");
        var agent = await CreateAgentAsync(projectId, "replay-archived-agent");
        const string idempotencyKey = "replay-after-agent-archive";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "preserve archived launch" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await LaunchReferencesAsync(first);

        using var archive = await _fixture.Client.DeleteAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}");
        archive.EnsureSuccessStatusCode();

        using var replay = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "preserve archived launch" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await LaunchReferencesAsync(replay));
    }

    [Fact]
    public async Task Launch_ReplayWithDifferentSuppliedAgentReference_Conflicts()
    {
        var projectId = await CreateProjectAsync("launch-replay-agent-reference");
        var agent = await CreateAgentAsync(projectId, "same-agent-different-reference");
        const string idempotencyKey = "different-agent-reference";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "same prompt" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await LaunchAsync(
            projectId,
            "same-agent-different-reference",
            new { prompt = "same prompt" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_ReplayWithWhitespacePrompt_ConflictsInsteadOfRevalidating()
    {
        var projectId = await CreateProjectAsync("launch-replay-whitespace-prompt");
        var agent = await CreateAgentAsync(projectId, "whitespace-prompt-agent");
        const string idempotencyKey = "different-whitespace-prompt";

        using var first = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "accepted prompt" },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await LaunchAsync(
            projectId,
            agent.Id,
            new { prompt = "   " },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Coordinator_DoesNotReturnLaunchResultUntilSetupCompletes()
    {
        var projectId = $"launch-setup-pending-{Guid.NewGuid():N}";
        const string idempotencyKey = "launch-setup-pending";
        var request = new AgentLaunchCoordinatorRequest(
            Prompt: "persist before accepting",
            AgentRef: "agent-1",
            Runtime: null,
            WorkspacePath: null,
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null);
        var coordinator = _fixture.Grains.GetGrain<IAgentLaunchCoordinatorGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, idempotencyKey));
        var command = new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: projectId,
            IdempotencyKey: idempotencyKey,
            AgentId: "agent-1",
            AgentName: "Agent",
            AgentInstructions: null,
            AgentConfigJson: "{",
            Model: null,
            Variant: null,
            Runtime: null,
            Prompt: request.Prompt,
            WorkspacePath: null,
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            Request: request);

        await Assert.ThrowsAsync<LaunchSetupPendingException>(() => coordinator.LaunchAsync(command));
        await Assert.ThrowsAsync<LaunchSetupPendingException>(() => coordinator.ResumeAsync(request));
    }

    private static async Task<LaunchReferences> LaunchReferencesAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        return new LaunchReferences(
            data.GetProperty("jobId").GetString(),
            data.GetProperty("sessionId").GetString(),
            data.GetProperty("inputId").GetString(),
            data.GetProperty("turnId").GetString());
    }

    private sealed record LaunchReferences(string? JobId, string? SessionId, string? InputId, string? TurnId);
}
