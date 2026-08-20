using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// Issue-512 T-002 specs for Unknown-safe launch reconciliation and
/// the composite launch-observation read API. Covers:
/// - launch and composite observation during queued / terminal states
/// - cross-project isolation returns 404
/// - launch 201 surfaces all four stable references plus the observation URL
/// Projection details for Unknown/recovering, terminal fields, project
/// isolation, and missing links are covered by the L0 assembler specs;
/// lifecycle/reconciliation behavior remains covered by AgentJob grain specs.
/// </summary>
[Collection("LaunchIntegration")]
public class AgentLaunchObservationRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentLaunchObservationRoutesSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_ReturnsAllFourStableReferencesAndObservationUrl()
    {
        var projectId = await CreateProjectAsync("obs-201-ids");
        var runnerId = $"obs-201-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-id-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "return four ids" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var data = launchPayload.GetProperty("data");

            var jobId = data.GetProperty("jobId").GetString();
            var sessionId = data.GetProperty("sessionId").GetString();
            var inputId = data.GetProperty("inputId").GetString();
            var turnId = data.GetProperty("turnId").GetString();
            var observationUrl = data.GetProperty("observationUrl").GetString();

            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            Assert.False(string.IsNullOrWhiteSpace(inputId));
            Assert.False(string.IsNullOrWhiteSpace(turnId));
            Assert.Equal(
                $"/api/projects/{projectId}/agent-jobs/{jobId}/launch-observation",
                observationUrl);
        }
        finally
        {
            await DrainDispatchAsync(runnerId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_DuringQueuedState_ReportsAcceptedInputAndPendingTurn()
    {
        var projectId = await CreateProjectAsync("obs-queued");
        var agent = await CreateAgentAsync(projectId, "obs-queued-agent");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "no runner online" });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var data = launchPayload.GetProperty("data");
        var jobId = data.GetProperty("jobId").GetString()!;

        // No runner registered → Job stays Pending. The Session's
        // first Input is accepted and the first Turn is queued.
        var observation = await ReadObservationAsync(projectId, jobId);
        Assert.NotNull(observation);
        var obs = observation!.Value.GetProperty("data");
        Assert.Equal("pending", obs.GetProperty("jobStatus").GetString());
        Assert.Equal("accepted", obs.GetProperty("inputAcceptance").GetString());
        Assert.Equal("queued", obs.GetProperty("turnStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("sessionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("inputId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("turnId").GetString()));
    }

    [Fact]
    public async Task Observation_DuringTerminalState_ReportsJobResultAndTurnResult()
    {
        var projectId = await CreateProjectAsync("obs-terminal");
        var runnerId = $"obs-terminal-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-terminal-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "complete me" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
            var claim = await ClaimPreparedAgentJobAsync(jobId, runnerId, projectId, sessionId);
            var persistence = _fixture.Persistence.Checkpoint(sessionId);
            var report = await jobGrain.ReportResultAsync(
                runnerId,
                claim.WorkId,
                new WorkResult(
                    Status: "completed",
                    Message: "all done",
                    Output: JSON.DeserializeElement("{}"),
                    ArtifactUploadIds: null,
                    ExitCode: 0));
            Assert.True(report.Accepted, "AgentJob rejected completed report");

            var observation = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(observation);
            var obs = observation!.Value.GetProperty("data");
            Assert.Equal("completed", obs.GetProperty("jobStatus").GetString());
            Assert.Equal("completed", obs.GetProperty("turnStatus").GetString());
            Assert.Equal("accepted", obs.GetProperty("inputAcceptance").GetString());
            // The Job terminal message and the Turn result are surfaced
            // through the same composite read.
            var turnResult = obs.GetProperty("turnResult");
            Assert.NotEqual(JsonValueKind.Null, turnResult.ValueKind);
            Assert.Equal("all done", turnResult.GetProperty("message").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_CrossProjectJob_Returns404()
    {
        var projectA = await CreateProjectAsync("obs-proj-a");
        var projectB = await CreateProjectAsync("obs-proj-b");
        var agent = await CreateAgentAsync(projectA, "obs-cross-agent");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectA, agent.Id, new { prompt = "cross project" });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var jobId = (await launch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("jobId").GetString()!;

        // Cross-project read must NOT return the Job — even though it
        // exists, it belongs to project A.
        using var crossRead = await _fixture.Client.GetAsync(
            $"/api/projects/{projectB}/agent-jobs/{jobId}/launch-observation");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);

        // Same-project read returns 200.
        using var sameRead = await _fixture.Client.GetAsync(
            $"/api/projects/{projectA}/agent-jobs/{jobId}/launch-observation");
        Assert.Equal(HttpStatusCode.OK, sameRead.StatusCode);
    }

    [Fact]
    public async Task Launch_RejectsUnknownJobIdOnObservationRoute_Returns404()
    {
        var projectId = await CreateProjectAsync("obs-not-found");

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-jobs/agent-job-launch-{Guid.NewGuid():N}/launch-observation");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<JsonElement?> ReadObservationAsync(string projectId, string jobId)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-jobs/{jobId}/launch-observation");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
