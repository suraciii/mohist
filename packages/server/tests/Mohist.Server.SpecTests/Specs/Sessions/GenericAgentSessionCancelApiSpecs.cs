using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("SessionControlIntegration")]
public class GenericAgentSessionCancelApiSpecs : GenericAgentSessionCancelApiTestSupport
{
    public GenericAgentSessionCancelApiSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Stop_QueuedTurnWithoutRunner_ReturnsCancelledAndPreservesRecords()
    {
        var (project, sessionId, turnId) = await CreateQueuedSessionForCancelAsync();
        var before = await ReadSessionEvidenceAsync(sessionId);
        var hub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
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
    public async Task Stop_ExecutingTurnSendsTurnTargetAndReturnsStopped()
    {
        var (project, sessionId) = await CreateCanonicalSessionForCancelAsync("agent-launch");
        var turnId = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync()).Id;
        var hub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("session.stop", new RunnerStopReply("stopped"));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("stopped", data.GetProperty("state").GetString());
        var invocation = Assert.Single(hub.Invocations);
        Assert.Equal("session.stop", invocation.Method);
        var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
        Assert.Equal(turnId, payload.GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task Stop_OfflineBeforeDispatchKeepsTurnClaimForRecovery()
    {
        var (project, sessionId, turnId) = await CreateExecutingSessionForCancelAsync();
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        tracker.Unregister(_runnerId);
        try
        {
            using var response = await PostStopAsync(project.Id, sessionId, turnId);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await Assert.ThrowsAsync<StopOperationInProgressException>(session.BeginFollowupAsync);
        }
        finally
        {
            tracker.Register(_runnerId, $"{_runnerId}-conn");
        }
    }

    [Fact]
    public async Task Stop_UnconfirmedReplySurfacesUnknownAndInterruptFlag()
    {
        var (project, sessionId, turnId, jobId) = await CreateExecutingLaunchSessionForStopAsync();
        var hub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        hub.Clear();
        hub.SetInvocationResponse("session.stop", new RunnerStopReply("unknown", true));

        using var response = await PostStopAsync(project.Id, sessionId, turnId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("unknown", data.GetProperty("state").GetString());
        Assert.True(data.GetProperty("interruptUnconfirmed").GetBoolean());
        Assert.Single(hub.Invocations);
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
        var turn = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Executing, turn.Status);

        await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

        turn = Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Unknown, turn.Status);
        Assert.Equal("unknown", (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync())!.Status);
    }

    private async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private Task<HttpResponseMessage> PostCancelAsync(string projectId, string sessionId, string turnId) =>
        PostStopAsync(projectId, sessionId, turnId);

    private async Task<HttpResponseMessage> PostStopAsync(string projectId, string sessionId, string turnId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/stop"
        )
        {
            Content = JsonContent.Create(new { turnId }),
        };
        request.Headers.Add("Idempotency-Key", $"stop-{Guid.NewGuid():N}");
        return await _client.SendAsync(request);
    }
}
