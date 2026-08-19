using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class WorkflowAgentSessionExecutionBoundaryApiSpecs : AgentSessionTestSupport
{
    public WorkflowAgentSessionExecutionBoundaryApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task WorkflowInput_WithoutAnAcceptedWorkflowBinding_ReturnsNoReceiptAndReusesItsTurn()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("workflow-input-binding-rejected");
        var request = new
        {
            runtimeSessionId = session.Id,
            runtime = "opencode",
            agentSessionId = session.Id,
            inputDeliveryId = "delivery-binding-rejected",
            taskRunId = "task-binding-rejected.1",
            workId = work.WorkId,
            runtimeEvents = new[]
            {
                new { type = RuntimeEventTypes.SessionInput, payload = new { text = "do not start" } }
            }
        };

        using var firstResponse = await _client.PostAsJsonAsync(RunnerAgentSessionRuntimeEventsPath(session), request);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstReceipt = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, firstReceipt.RootElement.ValueKind);
        Assert.Empty(firstReceipt.RootElement.EnumerateArray());

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var firstTurn = Assert.Single(await grain.ListTurnsAsync());
        Assert.Equal("delivery-binding-rejected", firstTurn.WorkflowExecution?.InputDeliveryId);
        Assert.Equal("task-binding-rejected.1", firstTurn.WorkflowExecution?.TaskRunId);

        using var replayResponse = await _client.PostAsJsonAsync(RunnerAgentSessionRuntimeEventsPath(session), request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        using var replayReceipt = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, replayReceipt.RootElement.ValueKind);
        Assert.Empty(replayReceipt.RootElement.EnumerateArray());
        Assert.Equal(firstTurn.Id, Assert.Single(await grain.ListTurnsAsync()).Id);
    }

    [Fact]
    public async Task WorkflowRuntimeEvents_WithoutExecutionIdentity_FailClosed()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("workflow-runtime-no-identity");

        using var response = await _client.PostAsJsonAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = RuntimeEventTypes.SessionActivity, payload = new { activity = "idle" } }
            }
        });

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {response.StatusCode}: {responseBody}");
        using var body = JsonDocument.Parse(responseBody);
        Assert.Equal("workflow_runtime_binding_required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkflowRuntimeEvents_ForReplacedAgentSession_AreRejectedBeforeAppend()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("workflow-runtime-replaced");

        using var response = await _client.PostAsJsonAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtime = "opencode",
            agentSessionId = "other-agent-session",
            agentTurnId = "turn-other",
            inputDeliveryId = "delivery-other",
            taskRunId = "task-other.1",
            workId = work.WorkId,
            runtimeEvents = new[]
            {
                new { type = "message.delta", payload = new { text = "stale", turnId = "turn-other" } }
            }
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("workflow_agent_session_changed", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SessionFollowupRuntimeEvents_RequireTheRecordedSessionAndTurn()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("session-followup-identity");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "continue",
            "test",
            "session-followup-identity"));

        var request = new
        {
            runtimeSessionId = session.Id,
            agentSessionId = session.Id,
            agentTurnId = accepted.TurnId,
            runtimeEvents = new[]
            {
                new
                {
                    type = RuntimeEventTypes.SessionInput,
                    payload = new { text = "continue", kind = "followup", turnId = accepted.TurnId }
                }
            }
        };

        using var stale = await _client.PostAsJsonAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{session.Id}/runtime-events",
            request with { agentSessionId = "other-agent-session" });
        var staleBody = await stale.Content.ReadAsStringAsync();
        Assert.True(stale.StatusCode == HttpStatusCode.Conflict, $"Expected 409, got {stale.StatusCode}: {staleBody}");
        Assert.Equal(AgentTurnStatus.Queued, Assert.Single(await grain.ListTurnsAsync()).Status);

        using var acceptedResponse = await _client.PostAsJsonAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{session.Id}/runtime-events",
            request);
        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await grain.ListTurnsAsync()).Status);
    }
}
