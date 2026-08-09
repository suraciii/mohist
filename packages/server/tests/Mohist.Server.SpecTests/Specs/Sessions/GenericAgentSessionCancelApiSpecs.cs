using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionCancelApiSpecs : GenericAgentSessionStopTestSupport
{
    public GenericAgentSessionCancelApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Cancel_QueuedTurnWithoutRunner_ReturnsCancelledAndPreservesRecords()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForStopAsync();
        var before = await ReadSessionEvidenceAsync(sessionId);
        var hub = Hub();
        hub.Clear();

        using var response = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("cancelled", data.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turn = Assert.Single(await session.ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Cancelled, turn.Status);
        Assert.Equal("idle", (await session.GetAsync())!.Status);
        var after = await ReadSessionEvidenceAsync(sessionId);
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.TranscriptTurns, after.TranscriptTurns);
        Assert.Equal(before.TranscriptParts, after.TranscriptParts);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Cancel_TerminalTurnReturnsAlreadyEndedAndDoesNotTouchOtherTurns()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForStopAsync();
        using var first = await PostCancelAsync(project.Id, sessionId, turnId);
        var hub = Hub();
        hub.Clear();

        using var second = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var data = await ReadDataAsync(second);
        Assert.Equal("turn-already-ended", data.GetProperty("state").GetString());
        Assert.Equal("cancelled", data.GetProperty("turnStatus").GetString());
        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Cancel_ExecutingTurnReportsExecutingAndDoesNotContactRunner()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForStopAsync();
        var hub = Hub();
        hub.Clear();

        using var response = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("executing", data.GetProperty("state").GetString());
        Assert.Equal("stop", data.GetProperty("action").GetString());
        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Stop_ExecutingTurnSendsTurnTargetAndReturnsStopped()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var info = await session.GetAsync() ?? throw new InvalidOperationException("session was not persisted");
        var hub = Hub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        using var wrongProject = await PostStopAsync($"{project.Id}-wrong", sessionId, turnId);
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
        using var wrongSession = await PostStopAsync(project.Id, "missing-session", turnId);
        Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);
        Assert.Empty(hub.Invocations);

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(["success", "data"], root.EnumerateObject().Select(property => property.Name).ToArray());
        var data = root.GetProperty("data");
        Assert.Equal(["state"], data.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("stopped", data.GetProperty("state").GetString());

        var invocation = Assert.Single(hub.Invocations);
        Assert.Equal("CancelAgentSession", invocation.Method);
        Assert.Equal(RunnerConnectionId, invocation.ConnectionId);
        var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
        Assert.Equal(
            ["target", "sessionId", "turnId", "operationId"],
            payload.EnumerateObject().Select(property => property.Name).ToArray());
        var target = payload.GetProperty("target");
        Assert.Equal(
            ["kind", "projectId", "sessionId", "binding"],
            target.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("generic", target.GetProperty("kind").GetString());
        Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
        Assert.Equal(info.Id, target.GetProperty("sessionId").GetString());
        Assert.Equal(turnId, payload.GetProperty("turnId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("operationId").GetString()));
        var binding = target.GetProperty("binding");
        Assert.Equal(
            ["runtime", "runtimeSessionId", "runnerId", "workDir"],
            binding.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(info.Runtime, binding.GetProperty("runtime").GetString());
        Assert.Equal(info.AgentSessionId, binding.GetProperty("runtimeSessionId").GetString());
        Assert.Equal(info.RunnerId, binding.GetProperty("runnerId").GetString());
        Assert.Equal(info.WorkDir, binding.GetProperty("workDir").GetString());
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task StopAndCancel_UnknownProjectSessionAndTurnReturnNotFoundEnvelope()
    {
        var (project, sessionId) = await CreateCanonicalSessionForStopAsync("agent-launch");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turnId = Assert.Single(await session.ListTurnsAsync()).Id;
        var hub = Hub();
        hub.Clear();

        foreach (var action in new[] { "cancel", "stop" })
        {
            using var unknownProject = await PostActionAsync(
                action,
                $"{project.Id}-missing",
                sessionId,
                turnId);
            await AssertNotFoundEnvelopeAsync(unknownProject, "Project not found");

            using var unknownSession = await PostActionAsync(
                action,
                project.Id,
                "missing-session",
                turnId);
            await AssertNotFoundEnvelopeAsync(
                unknownSession,
                "Agent session missing-session not found");

            using var unknownTurn = await PostActionAsync(
                action,
                project.Id,
                sessionId,
                "missing-turn");
            await AssertNotFoundEnvelopeAsync(unknownTurn, "Turn missing-turn not found");
        }

        Assert.Empty(hub.Invocations);
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    [Fact]
    public async Task Cancel_LaterTurnDoesNotChangeTerminalLaunchJob()
    {
        var (project, sessionId, initialTurnId, jobId, _) =
            await CreateExecutingLaunchSessionForStopAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).FailAsync("terminal-before-followup");
        await session.MarkTurnTerminalAsync(initialTurnId, AgentTurnStatus.Failed, null);
        var turnId = $"turn-{Guid.NewGuid():N}";

        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}",
            turnId,
            "later follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        var hub = Hub();
        hub.Clear();

        using var response = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("executing", (await ReadDataAsync(response)).GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);
        Assert.Equal(
            AgentJobStatus.Failed,
            await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
        await AssertTrackedLaunchResourcesReleasedAsync();
        await AssertNoStopOwnedResourcesAsync(project.Id, sessionId, _runnerId);
    }

    private Task<HttpResponseMessage> PostActionAsync(
        string action,
        string projectId,
        string sessionId,
        string turnId) =>
        action switch
        {
            "cancel" => PostCancelAsync(projectId, sessionId, turnId),
            "stop" => PostStopAsync(projectId, sessionId, turnId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown session action"),
        };

    private async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(["success", "data"], root.EnumerateObject().Select(property => property.Name).ToArray());
        return root.GetProperty("data").Clone();
    }

    private static async Task AssertNotFoundEnvelopeAsync(
        HttpResponseMessage response,
        string expectedError)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            ["success", "error", "code"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.Equal(expectedError, root.GetProperty("error").GetString());
        Assert.Equal("not_found", root.GetProperty("code").GetString());
    }

    private Task<HttpResponseMessage> PostCancelAsync(
        string projectId,
        string sessionId,
        string turnId) =>
        _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/cancel",
            new { turnId });

    private Task<HttpResponseMessage> PostStopAsync(
        string projectId,
        string sessionId,
        string turnId) =>
        _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/stop",
            new { turnId });

    private RecordingRunnerHubContext Hub() =>
        _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
        ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
}
