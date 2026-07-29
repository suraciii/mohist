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
public class GenericAgentSessionCancelApiSpecs : GenericAgentSessionCancelApiTestSupport
{
    public GenericAgentSessionCancelApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Cancel_QueuedTurnWithoutRunner_ReturnsCancelledAndPreservesRecords()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForCancelAsync();
        var before = await ReadSessionEvidenceAsync(sessionId);
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();

        using var response = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("cancelled", data.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);
        var turn = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync());
        Assert.Equal("cancelled", turn.Status.ToString().ToLowerInvariant());
        Assert.Equal("idle", (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync())!.Status);
        var after = await ReadSessionEvidenceAsync(sessionId);
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.TranscriptTurns, after.TranscriptTurns);
        Assert.Equal(before.TranscriptParts, after.TranscriptParts);
    }

    [Fact]
    public async Task Cancel_TerminalTurnReturnsAlreadyEndedAndDoesNotTouchOtherTurns()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForCancelAsync();
        using var first = await PostCancelAsync(project.Id, sessionId, turnId);
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();

        using var second = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var data = await ReadDataAsync(second);
        Assert.Equal("turn-already-ended", data.GetProperty("state").GetString());
        Assert.Equal("cancelled", data.GetProperty("turnStatus").GetString());
        Assert.Empty(hub.Invocations);
    }

    [Fact]
    public async Task Cancel_ExecutingTurnReportsExecutingAndDoesNotContactRunner()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();

        using var response = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("executing", data.GetProperty("state").GetString());
        Assert.Equal("stop", data.GetProperty("action").GetString());
        Assert.Empty(hub.Invocations);
    }

    [Fact]
    public async Task Stop_ExecutingTurnSendsTurnTargetAndReturnsStopped()
    {
        var (project, sessionId) = await CreateCanonicalSessionForCancelAsync("agent-launch");
        var turnId = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync()).Id;
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("stopped", data.GetProperty("state").GetString());
        var invocation = Assert.Single(hub.Invocations);
        Assert.Equal("CancelAgentSession", invocation.Method);
        var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
        Assert.Equal(turnId, payload.GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task Stop_QueuedTurnDirectsCallerToCancelWithoutContactingRunner()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForCancelAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("queued", data.GetProperty("state").GetString());
        Assert.Equal("cancel", data.GetProperty("action").GetString());
        Assert.Empty(hub.Invocations);
    }

    [Fact]
    public async Task Stop_UnconfirmedReplySurfacesUnknownAndInterruptFlag()
    {
        var (project, sessionId, turnId, jobId) = await CreateExecutingLaunchSessionForStopAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("unknown", true));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("unknown", data.GetProperty("state").GetString());
        Assert.True(data.GetProperty("interruptUnconfirmed").GetBoolean());
        Assert.Single(hub.Invocations);
        Assert.Equal(AgentJobStatus.Unknown, await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
        var turn = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Unknown, turn.Status);
        Assert.Equal("unknown", (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync())!.Status);
    }

    [Fact]
    public async Task Stop_UnconfirmedReplyMarksFollowupTurnAndSessionUnknownWithoutRunnerActivity()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("unknown", true));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("unknown", data.GetProperty("state").GetString());
        Assert.True(data.GetProperty("interruptUnconfirmed").GetBoolean());
        var turn = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Unknown, turn.Status);
        Assert.Equal("unknown", (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync())!.Status);
    }

    [Fact]
    public async Task Stop_TerminalTurnReturnsAlreadyEndedWithoutContactingRunner()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForCancelAsync();
        using var cancel = await PostCancelAsync(project.Id, sessionId, turnId);
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("turn-already-ended", data.GetProperty("state").GetString());
        Assert.Equal("cancelled", data.GetProperty("turnStatus").GetString());
        Assert.Empty(hub.Invocations);
    }

    private async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private Task<HttpResponseMessage> PostCancelAsync(string projectId, string sessionId, string turnId) =>
        _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/cancel",
            new { turnId });

    private Task<HttpResponseMessage> PostStopAsync(string projectId, string sessionId, string turnId) =>
        _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/stop",
            new { turnId });
}
