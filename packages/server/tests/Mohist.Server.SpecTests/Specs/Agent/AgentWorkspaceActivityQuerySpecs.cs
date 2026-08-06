using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent;

/// <summary>
/// Runner-owned activity query for a managed-worktree child session:
/// the five states (active / idle+idleSince / pending / not-found /
/// unknown), the durable <c>IdleSince</c> driven by the injected clock,
/// and the runner / project authorization gate that must not masquerade
/// as not-found.
/// </summary>
[Collection("MohistIntegration")]
public class AgentWorkspaceActivityQuerySpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private const string Runtime = "opencode";
    private const string RuntimeSession = "activity-spec-runtime";

    public AgentWorkspaceActivityQuerySpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    // ---- five states ----

    [Fact]
    public async Task Activity_UnknownSession_ReturnsNotFoundState_200()
    {
        var projectId = NewId("project");
        var runnerId = NewId("runner");
        var childSessionId = NewId("session");

        using var response = await QueryAsync(runnerId, projectId, childSessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("not-found", data.GetProperty("state").GetString());
        Assert.False(data.TryGetProperty("idleSince", out _));
    }

    [Fact]
    public async Task Activity_ProvisionalLaunch_ReturnsPending_NoIdleSince()
    {
        var (runnerId, projectId, sessionId, _) = await OpenAsync(
            visibility: AgentLaunchVisibility.Provisional);

        using var response = await QueryAsync(runnerId, projectId, sessionId);

        var data = await ReadDataAsync(response);
        Assert.Equal("pending", data.GetProperty("state").GetString());
        Assert.False(data.TryGetProperty("idleSince", out _));
    }

    [Fact]
    public async Task Activity_FreshlyOpenedIdle_ReportsIdleSince_FromInjectedClock()
    {
        var clockInstant = _fixture.TimeProvider.GetUtcNow();
        var (runnerId, projectId, sessionId, _) = await OpenAsync();

        using var response = await QueryAsync(runnerId, projectId, sessionId);

        var data = await ReadDataAsync(response);
        Assert.Equal("idle", data.GetProperty("state").GetString());
        var idleSince = data.GetProperty("idleSince").GetString();
        Assert.Equal(clockInstant.UtcDateTime.ToString("o"), idleSince);
    }

    [Fact]
    public async Task Activity_ExecutingTurn_ReturnsActive_ClearsIdleSince()
    {
        var (runnerId, projectId, sessionId, grain) = await OpenAsync();
        await BeginExecutingTurnAsync(grain, "turn-active");

        using var response = await QueryAsync(runnerId, projectId, sessionId);

        var data = await ReadDataAsync(response);
        Assert.Equal("active", data.GetProperty("state").GetString());
        Assert.False(data.TryGetProperty("idleSince", out _));
    }

    [Fact]
    public async Task Activity_UnknownRuntimeState_ReturnsUnknown_NoIdleSince()
    {
        var (runnerId, projectId, sessionId, grain) = await OpenAsync();
        await BeginExecutingTurnAsync(grain, "turn-unknown");
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    "{\"activity\":\"unknown\"}"),
            },
            RuntimeSession));

        using var response = await QueryAsync(runnerId, projectId, sessionId);

        var data = await ReadDataAsync(response);
        Assert.Equal("unknown", data.GetProperty("state").GetString());
        // Unknown is fail-closed: a stale idle time must never travel with it.
        Assert.False(data.TryGetProperty("idleSince", out _));
    }

    // ---- fake clock drives IdleSince on the idle transition ----

    [Fact]
    public async Task IdleSince_AdvancesWithClock_OnReturnToIdle()
    {
        var (runnerId, projectId, sessionId, grain) = await OpenAsync();
        var firstIdle = await IdleSince(await QueryAsync(runnerId, projectId, sessionId));

        // Turn goes active (clears IdleSince), then completes back to idle at
        // a later injected-clock instant: IdleSince must reflect that instant.
        await BeginExecutingTurnAsync(grain, "turn-clock");
        Assert.Null(await IdleSince(await QueryAsync(runnerId, projectId, sessionId)));

        var advanced = _fixture.TimeProvider.GetUtcNow().Add(TimeSpan.FromMinutes(42));
        _fixture.TimeProvider.SetUtcNow(advanced);
        await grain.MarkTurnTerminalAsync("turn-clock", AgentTurnStatus.Completed, null);

        var secondIdle = await IdleSince(await QueryAsync(runnerId, projectId, sessionId));

        Assert.NotNull(secondIdle);
        Assert.Equal(advanced.UtcDateTime.ToString("o"), secondIdle);
        Assert.True(DateTime.Parse(secondIdle!) > DateTime.Parse(firstIdle!));
    }

    // ---- authorization gate (must not masquerade as not-found) ----

    [Fact]
    public async Task Activity_RunnerMismatch_Returns403_NotNotFound()
    {
        var (_, projectId, sessionId, _) = await OpenAsync(runnerId: "runner-owner");

        using var response = await QueryAsync("runner-impostor", projectId, sessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runner_not_authorized", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Activity_ProjectMismatch_Returns403_NotNotFound()
    {
        var (runnerId, _, sessionId, _) = await OpenAsync();

        using var response = await QueryAsync(runnerId, "other-project", sessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("project_mismatch", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("data", out _));
    }

    // ---- helpers ----

    private async Task<(string RunnerId, string ProjectId, string SessionId, IAgentSessionGrain Grain)> OpenAsync(
        string? runnerId = null,
        AgentLaunchVisibility visibility = AgentLaunchVisibility.Visible)
    {
        var rid = runnerId ?? NewId("runner");
        var pid = NewId("project");
        var sid = NewId("session");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sid);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: rid,
            AgentRuntime: Runtime,
            WorkDir: "/tmp/activity-spec",
            Metadata: SessionMetadata(pid),
            LaunchVisibility: visibility));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            RuntimeSession,
            WorkDir: "/tmp/activity-spec"));
        return (rid, pid, sid, grain);
    }

    private static async Task BeginExecutingTurnAsync(IAgentSessionGrain grain, string turnId)
    {
        await grain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"{turnId}-input", turnId, "do work", "generic-followup"));
        await grain.MarkTurnExecutingAsync(turnId);
    }

    private static AgentSessionMetadata SessionMetadata(string projectId) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = "activity-spec-agent",
        });

    private Task<HttpResponseMessage> QueryAsync(string runnerId, string projectId, string childSessionId) =>
        _fixture.Client.GetAsync(
            $"/api/runner/{runnerId}/agent-workspaces/{projectId}/{childSessionId}/activity");

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data");
    }

    private static async Task<string?> IdleSince(HttpResponseMessage response)
    {
        var data = await ReadDataAsync(response);
        return data.TryGetProperty("idleSince", out var idle) && idle.ValueKind == JsonValueKind.String
            ? idle.GetString()
            : null;
    }

    private static string NewId(string prefix) => $"{prefix}-activity-{Guid.NewGuid():N}";
}
