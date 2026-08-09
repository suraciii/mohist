using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("MohistIntegration")]
public sealed class RunnerAgentSessionReconciliationApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerAgentSessionReconciliationApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReconcileList_UsesDurableRunnerBindingWithoutConnectionTrackerRegistration()
    {
        var runnerId = $"runner-reconcile-{Guid.NewGuid():N}";
        var matching = await CreateBoundSessionAsync(runnerId, "runtime-matching", AgentSessionActivity.Unknown);
        await CreateBoundSessionAsync(runnerId, "runtime-idle", AgentSessionActivity.Idle);
        await CreateBoundSessionAsync($"other-{runnerId}", "runtime-other", AgentSessionActivity.Unknown);

        using var response = await _fixture.Client.GetAsync($"/api/runner/{runnerId}/agent-sessions/reconcile");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(payload.EnumerateArray());
        Assert.Equal(matching, item.GetProperty("sessionId").GetString());
        Assert.Equal("opencode", item.GetProperty("runtime").GetString());
        Assert.Equal("runtime-matching", item.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("/work", item.GetProperty("workDir").GetString());
    }

    [Fact]
    public async Task ReconcileMissing_UnknownSession_SettlesIdleAndRejectsStaleRetry()
    {
        var runnerId = $"runner-missing-{Guid.NewGuid():N}";
        var sessionId = await CreateBoundSessionAsync(runnerId, "runtime-missing", AgentSessionActivity.Unknown);
        var request = new
        {
            expectedRunnerId = runnerId,
            expectedRuntime = "opencode",
            expectedRuntimeSessionId = "runtime-missing",
            replacementRuntimeSessionId = "runtime-replacement",
        };

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/agent-sessions/{sessionId}/reconcile-missing",
            request);

        response.EnsureSuccessStatusCode();
        var state = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        Assert.Equal("runtime-replacement", state?.AgentSessionId);
        Assert.Equal("idle", state?.Status);

        using var stale = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/agent-sessions/{sessionId}/reconcile-missing",
            request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale_binding", staleBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkflowTurnReservation_IsIdempotentAndRollsBackQueuedReservation()
    {
        var projectId = $"project-workflow-turn-{Guid.NewGuid():N}";
        var workflowRunId = $"workflow-turn-{Guid.NewGuid():N}";
        var sessionName = "review";
        var sessionId = $"session-workflow-turn-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-workflow-turn",
            AgentRuntime: "pi",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
                .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
                .WithLabel(AgentSessionQueryMetadataKeys.SessionName, sessionName)));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-workflow-turn", "/work"));

        var request = new
        {
            inputId = "workflow-input-work-1",
            turnId = "workflow-turn-work-1",
            prompt = "public prompt",
            source = "workflow",
        };
        var route = $"/api/runner/runner-workflow-turn/sessions/{projectId}/{workflowRunId}/{sessionName}/turn";

        using var first = await _fixture.Client.PostAsJsonAsync(route, request);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", firstBody.GetProperty("status").GetString());
        Assert.True(firstBody.GetProperty("admissionReady").GetBoolean());
        var operationId = firstBody.GetProperty("operationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(operationId));

        using var duplicate = await _fixture.Client.PostAsJsonAsync(route, request);
        duplicate.EnsureSuccessStatusCode();
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", duplicateBody.GetProperty("status").GetString());
        Assert.Equal(operationId, duplicateBody.GetProperty("operationId").GetString());
        Assert.Single(await grain.ListTurnsAsync());

        using var rollback = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/runner-workflow-turn/sessions/{projectId}/{workflowRunId}/{sessionName}/turn/abandon",
            new { inputId = request.inputId, turnId = request.turnId });
        rollback.EnsureSuccessStatusCode();
        var rollbackBody = await rollback.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("abandoned", rollbackBody.GetProperty("status").GetString());
        Assert.Empty(await grain.ListTurnsAsync());

        var request2 = new
        {
            inputId = "workflow-input-work-2",
            turnId = "workflow-turn-work-2",
            prompt = "public prompt 2",
            source = "workflow",
        };
        using var second = await _fixture.Client.PostAsJsonAsync(route, request2);
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var operationId2 = secondBody.GetProperty("operationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(operationId2));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    $$"""{"activity":"idle","status":"completed","operationId":"{{operationId2}}","turnId":"{{request2.turnId}}"}""")
            },
            "runtime-workflow-turn"));
        var completed = await grain.ResolveTurnControlAsync(request2.turnId);
        Assert.Equal(AgentTurnStatus.Completed, completed?.Status);
    }

    private async Task<string> CreateBoundSessionAsync(
        string runnerId,
        string runtimeSessionId,
        AgentSessionActivity activity)
    {
        var sessionId = $"session-reconcile-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, "project-reconcile")
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
                .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, $"workflow-{sessionId}")
                .WithLabel(AgentSessionQueryMetadataKeys.SessionName, "build")));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        if (activity != AgentSessionActivity.Idle)
        {
            var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                new[]
                {
                    new AgentSessionRuntimeEventInput(
                        RuntimeEventTypes.SessionActivity,
                        $"{{\"activity\":\"{activity.ToString().ToLowerInvariant()}\"}}")
                },
                runtimeSessionId));
            await persistence.WaitAsync();
        }
        return sessionId;
    }
}
