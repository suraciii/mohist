using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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

    /// <summary>
    /// A participant failure at each coordinator fence must surface as a
    /// <c>503 launch_setup_pending</c> response from the launch route
    /// (never <c>201</c>), and the same Idempotency-Key must recover to a
    /// single accepted launch once the failure clears. One fact per fence
    /// so a regression at any boundary is isolated.
    /// </summary>
    [Theory]
    [InlineData(LaunchParticipantGate.PrepareJob)]
    [InlineData(LaunchParticipantGate.EnsureInitialLaunch)]
    [InlineData(LaunchParticipantGate.SubmitJob)]
    public async Task Launch_ParticipantFailureAtFence_Returns503AndRecoversWithSameKey(
        LaunchParticipantGate gate)
    {
        var projectId = await CreateProjectAsync($"launch-fence-{gate}");
        var agent = await CreateAgentAsync(projectId, "fence-agent");
        var idempotencyKey = $"fence-{gate}-{Guid.NewGuid():N}";
        var body = new { prompt = "recover across the fence" };

        _fixture.LaunchFaults.FailNext(gate, times: 1);

        using var failing = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failing.StatusCode);
        Assert.Equal(
            "launch_setup_pending",
            (await failing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var recovered = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        var original = await LaunchReferencesAsync(recovered);

        // The fence no longer fails, so a resume with the same key must
        // return the persisted outcome rather than a new launch.
        _fixture.LaunchFaults.StopFailing(gate);
        using var resumed = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, resumed.StatusCode);
        Assert.Equal(original, await LaunchReferencesAsync(resumed));
        Assert.Single(_fixture.LaunchFaults.CommandIds(gate).Distinct(StringComparer.Ordinal));

        await AssertInitialLaunchChildStateAsync(
            original,
            inputAcceptance: AgentSessionInputAcceptance.Accepted,
            turnStatus: AgentTurnStatus.Queued);
    }

    /// <summary>
    /// A launch blocked at the Session fence leaves a prepared Job but
    /// no accepted Input/Turn; recovery must not duplicate either child.
    /// Verifies the partial-state shape the review calls out: the
    /// recovered Session holds exactly one accepted Input and one Turn.
    /// </summary>
    [Fact]
    public async Task Launch_RecoveryAfterSessionFenceFailure_RecordsSingleInputAndTurn()
    {
        var projectId = await CreateProjectAsync("launch-fence-session-children");
        var agent = await CreateAgentAsync(projectId, "session-children-agent");
        var idempotencyKey = $"fence-session-{Guid.NewGuid():N}";
        var body = new { prompt = "single input and turn after recovery" };

        _fixture.LaunchFaults.FailNext(LaunchParticipantGate.EnsureInitialLaunch, times: 2);
        for (var i = 0; i < 2; i++)
        {
            using var pending = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        }

        _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
        using var recovered = await LaunchAsync(projectId, agent.Id, body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        var refs = await LaunchReferencesAsync(recovered);

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(refs.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        Assert.NotNull(initial);
        Assert.Equal(refs.InputId, initial!.Input?.Id);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, initial.Input?.Acceptance);
        Assert.Equal(refs.TurnId, initial.Turn?.Id);
        Assert.Equal(AgentTurnStatus.Queued, initial.Turn?.Status);
    }

    private async Task AssertInitialLaunchChildStateAsync(
        LaunchReferences refs,
        AgentSessionInputAcceptance inputAcceptance,
        AgentTurnStatus turnStatus)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(refs.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        Assert.NotNull(initial);
        Assert.Equal(refs.InputId, initial!.Input?.Id);
        Assert.Equal(inputAcceptance, initial.Input?.Acceptance);
        Assert.Equal(refs.TurnId, initial.Turn?.Id);
        Assert.Equal(turnStatus, initial.Turn?.Status);
    }

    private static async Task<LaunchReferences> LaunchReferencesAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        return new LaunchReferences(
            data.GetProperty("jobId").GetString() ?? string.Empty,
            data.GetProperty("sessionId").GetString() ?? string.Empty,
            data.GetProperty("inputId").GetString() ?? string.Empty,
            data.GetProperty("turnId").GetString() ?? string.Empty);
    }

    private sealed record LaunchReferences(string JobId, string SessionId, string InputId, string TurnId);
}
