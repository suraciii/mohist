using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
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

    [Theory]
    [InlineData("completed", AgentTurnStatus.Completed)]
    [InlineData("failed", AgentTurnStatus.Failed)]
    public async Task Stop_UnconfirmedFollowupTurnReconcilesOnCorrelatedTerminalRuntimeActivity(
        string runtimeStatus,
        AgentTurnStatus expectedTurnStatus)
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("unknown", true));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var runtimeSessionId = (await session.GetAsync())!.AgentSessionId
            ?? throw new InvalidOperationException("Agent session runtime identity was not created.");
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"{runtimeStatus}\",\"turnId\":\"{turnId}\"}}") },
            runtimeSessionId));

        var turn = Assert.Single(await session.ListTurnsAsync());
        Assert.Equal(expectedTurnStatus, turn.Status);
        Assert.Equal("idle", (await session.GetAsync())!.Status);
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

    [Fact]
    public async Task Stop_TargetTerminalBeforeRunnerReplyDoesNotAdmitLaterTurn()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponseFactory("CancelAgentSession", _ => CompleteTargetBeforeStopReplyAsync(session, turnId));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stopped", (await ReadDataAsync(response)).GetProperty("state").GetString());
        Assert.Single(hub.Invocations);
        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task Stop_RunnerWithoutReplyKeepsTurnClaimUntilAConfirmedRuntimeFactArrives()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponseFactory("CancelAgentSession", _ => CompleteTargetWithoutStopReplyAsync(session, turnId));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);
    }

    [Fact]
    public async Task Stop_ConfirmedRuntimeFactReleasesTurnClaimAfterReplyLoss()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponseFactory("CancelAgentSession", _ => CompleteTargetWithoutStopReplyAsync(session, turnId));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = JsonSerializer.SerializeToElement(Assert.Single(hub.Invocations).Arguments.Single());
        var operationId = payload.GetProperty("operationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(operationId));
        var runtimeSessionId = (await session.GetAsync())!.AgentSessionId!;
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\",\"stopOperationId\":\"{operationId}\"}}") },
            runtimeSessionId));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task Cancel_LaterTurnDoesNotChangeTerminalLaunchJob()
    {
        var (project, sessionId, turnId, jobId) = await CreateTerminalLaunchWithExecutingFollowupAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();

        using var response = await PostCancelAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("executing", (await ReadDataAsync(response)).GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);
        Assert.Equal(AgentJobStatus.Failed, await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
    }

    [Fact]
    public async Task Stop_LaterTurnDoesNotChangeTerminalLaunchJob()
    {
        var (project, sessionId, turnId, jobId) = await CreateTerminalLaunchWithExecutingFollowupAsync();
        var hub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stopped", (await ReadDataAsync(response)).GetProperty("state").GetString());
        Assert.Equal(AgentJobStatus.Failed, await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetStatusAsync());
    }

    private async Task<(ProjectRef Project, string SessionId, string TurnId, string JobId)> CreateTerminalLaunchWithExecutingFollowupAsync()
    {
        var (project, sessionId, initialTurnId, jobId) = await CreateExecutingLaunchSessionForStopAsync();
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
        return (project, sessionId, turnId, jobId);
    }

    private static async Task<RunnerStopReply?> CompleteTargetBeforeStopReplyAsync(
        IAgentSessionGrain session,
        string turnId)
    {
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Completed, null);
        await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);
        return new RunnerStopReply("stopped");
    }

    private static async Task<RunnerStopReply?> CompleteTargetWithoutStopReplyAsync(
        IAgentSessionGrain session,
        string turnId)
    {
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Completed, null);
        return null;
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
