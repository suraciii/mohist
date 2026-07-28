using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
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
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                new[]
                {
                    new AgentSessionRuntimeEventInput(
                        RuntimeEventTypes.SessionActivity,
                        $"{{\"activity\":\"{activity.ToString().ToLowerInvariant()}\"}}")
                },
                runtimeSessionId));
            await grain.WaitForPersistenceAsync(_fixture.Persistence);
        }
        return sessionId;
    }
}
